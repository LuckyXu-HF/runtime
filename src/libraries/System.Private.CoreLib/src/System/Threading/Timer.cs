// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace System.Threading
{
    public delegate void TimerCallback(object? state);

    // TimerQueue maintains a list of active timers.  We use a single native timer to schedule all managed timers
    // in the process.
    //
    // Perf assumptions:  We assume that timers are created and destroyed frequently, but rarely actually fire.
    // There are roughly two types of timer:
    //
    //  - timeouts for operations.  These are created and destroyed very frequently, but almost never fire, because
    //    the whole point is that the timer only fires if something has gone wrong.
    //
    //  - scheduled background tasks.  These typically do fire, but they usually have quite long durations.
    //    So the impact of spending a few extra cycles to fire these is negligible.
    //
    // Because of this, we want to choose a data structure with very fast insert and delete times, and we can live
    // with linear traversal times when firing timers.  However, we still want to minimize the number of timers
    // we need to traverse while doing the linear walk: in cases where we have lots of long-lived timers as well as
    // lots of short-lived timers, when the short-lived timers fire, they incur the cost of walking the long-lived ones.
    //
    // The data structure we've chosen is an unordered doubly-linked list of active timers.  This gives O(1) insertion
    // and removal, and O(N) traversal when finding expired timers.  We maintain two such lists: one for all of the
    // timers that'll next fire within a certain threshold, and one for the rest.
    //
    // Note that all instance methods of this class require that the caller hold a lock on the TimerQueue instance.
    // We partition the timers across multiple TimerQueues, each with its own lock and set of short/long lists,
    // in order to minimize contention when lots of threads are concurrently creating and destroying timers often.
    [DebuggerDisplay("Count = {CountForDebugger}")]
    [DebuggerTypeProxy(typeof(TimerQueueDebuggerTypeProxy))]
    internal sealed partial class TimerQueue
    {
        #region Shared TimerQueue instances
        /// <summary>Mapping from a tick count to a time to use when debugging to translate tick count values.</summary>
        internal static readonly (long TickCount, DateTime Time) s_tickCountToTimeMap = (TickCount64, DateTime.UtcNow);

        public static TimerQueue[] Instances { get; } = CreateTimerQueues();

        private static TimerQueue[] CreateTimerQueues()
        {
            var queues = new TimerQueue[Environment.ProcessorCount];
            for (int i = 0; i < queues.Length; i++)
            {
                queues[i] = new TimerQueue(i);
            }
            return queues;
        }

        // This method is not thread-safe and should only be used from the debugger.
        private int CountForDebugger
        {
            get
            {
                int count = 0;
                foreach (TimerQueueTimer _ in GetTimersForDebugger())
                {
                    count++;
                }

                return count;
            }
        }

        // This method is not thread-safe and should only be used from the debugger.
        internal IEnumerable<TimerQueueTimer> GetTimersForDebugger()
        {
            // This should ideally take lock(this), but doing so can hang the debugger
            // if another thread holds the lock.  It could instead use Monitor.TryEnter,
            // but doing so doesn't work while dump debugging.  So, it doesn't take the
            // lock at all; it's theoretically possible but very unlikely this could result
            // in a circular list that causes the debugger to hang, too.

            for (TimerQueueTimer? timer = _shortTimers; timer != null; timer = timer._next)
            {
                yield return timer;
            }

            for (TimerQueueTimer? timer = _longTimers; timer != null; timer = timer._next)
            {
                yield return timer;
            }
        }

        private sealed class TimerQueueDebuggerTypeProxy
        {
            private readonly TimerQueue _queue;

            public TimerQueueDebuggerTypeProxy(TimerQueue queue)
            {
                ArgumentNullException.ThrowIfNull(queue);

                _queue = queue;
            }

            [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
            public TimerQueueTimer[] Items => new List<TimerQueueTimer>(_queue.GetTimersForDebugger()).ToArray();
        }

        #endregion

        #region interface to native timer

        private bool _isTimerScheduled;
        private long _currentTimerStartTicks;
        private uint _currentTimerDuration;

        private bool EnsureTimerFiresBy(uint requestedDuration)
        {
            // The VM's timer implementation does not work well for very long-duration timers.
            // So we limit our native timer duration to a "small" value.
            // This may cause us to attempt to fire timers early, but that's ok -
            // we'll just see that none of our timers has actually reached its due time,
            // and schedule the native timer again.
            const uint maxPossibleDuration = 0x0fffffff;
            uint actualDuration = Math.Min(requestedDuration, maxPossibleDuration);

            if (_isTimerScheduled)
            {
                long elapsed = TickCount64 - _currentTimerStartTicks;
                if (elapsed >= _currentTimerDuration)
                    return true; // the timer's about to fire

                uint remainingDuration = _currentTimerDuration - (uint)elapsed;
                if (actualDuration >= remainingDuration)
                    return true; // the timer will fire earlier than this request
            }

            if (SetTimer(actualDuration))
            {
                _isTimerScheduled = true;
                _currentTimerStartTicks = TickCount64;
                _currentTimerDuration = actualDuration;
                return true;
            }

            return false;
        }

        #endregion

        #region Firing timers

        // The two lists of timers that are part of this TimerQueue.  They conform to a single guarantee:
        // no timer in _longTimers has an absolute next firing time <= _currentAbsoluteThreshold.
        // That way, when FireNextTimers is invoked, we always process the short list, and we then only
        // process the long list if the current time is greater than _currentAbsoluteThreshold (or
        // if the short list is now empty and we need to process the long list to know when to next
        // invoke FireNextTimers).
        private TimerQueueTimer? _shortTimers;
        private TimerQueueTimer? _longTimers;

        // The current threshold, an absolute time where any timers scheduled to go off at or
        // before this time must be queued to the short list.
        private long _currentAbsoluteThreshold = TickCount64 + ShortTimersThresholdMilliseconds;

        // Default threshold that separates which timers target _shortTimers vs _longTimers. The threshold
        // is chosen to balance the number of timers in the small list against the frequency with which
        // we need to scan the long list.  It's thus somewhat arbitrary and could be changed based on
        // observed workload demand. The larger the number, the more timers we'll likely need to enumerate
        // every time the timer fires, but also the more likely it is that when it does we won't
        // need to look at the long list because the current time will be <= _currentAbsoluteThreshold.
        private const int ShortTimersThresholdMilliseconds = 333;

        // Lock shared by the TimerQueue and associated TimerQueueTimer instances
        internal Lock SharedLock { get; } = new Lock();

        // Fire any timers that have expired, and update the native timer to schedule the rest of them.
        // We're in a thread pool work item here, and if there are multiple timers to be fired, we want
        // to queue all but the first one.  The first may can then be invoked synchronously or queued,
        // a task left up to our caller, which might be firing timers from multiple queues.
        private void FireNextTimers()
        {
            // We fire the first timer on this thread; any other timers that need to be fired
            // are queued to the ThreadPool.
            TimerQueueTimer? timerToFireOnThisThread = null;

            lock (SharedLock)
            {
                // Since we got here, that means our previous timer has fired.
                _isTimerScheduled = false;
                bool haveTimerToSchedule = false;
                uint nextTimerDuration = uint.MaxValue;

                long nowTicks = TickCount64;

                // Sweep through the "short" timers.  If the current tick count is greater than
                // the current threshold, also sweep through the "long" timers.  Finally, as part
                // of sweeping the long timers, move anything that'll fire within the next threshold
                // to the short list.  It's functionally ok if more timers end up in the short list
                // than is truly necessary (but not the opposite).
                TimerQueueTimer? timer = _shortTimers;
                for (int listNum = 0; listNum < 2; listNum++) // short == 0, long == 1
                {
                    while (timer != null)
                    {
                        Debug.Assert(timer._dueTime != Timeout.UnsignedInfinite, "A timer in the list must have a valid due time.");

                        // Save off the next timer to examine, in case our examination of this timer results
                        // in our deleting or moving it; we'll continue after with this saved next timer.
                        TimerQueueTimer? next = timer._next;

                        long elapsed = nowTicks - timer._startTicks;
                        long remaining = timer._dueTime - elapsed;
                        if (remaining <= 0)
                        {
                            // Timer is ready to fire.
                            timer._everQueued = true;

                            if (timer._period != Timeout.UnsignedInfinite)
                            {
                                // This is a repeating timer; schedule it to run again.

                                // Discount the extra amount of time that has elapsed since the previous firing time to
                                // prevent timer ticks from drifting.  If enough time has already elapsed for the timer to fire
                                // again, meaning the timer can't keep up with the short period, have it fire 1 ms from now to
                                // avoid spinning without a delay.
                                timer._startTicks = nowTicks;
                                long elapsedForNextDueTime = elapsed - timer._dueTime;
                                timer._dueTime = (elapsedForNextDueTime < timer._period) ?
                                    timer._period - (uint)elapsedForNextDueTime :
                                    1;

                                // Update the timer if this becomes the next timer to fire.
                                if (timer._dueTime < nextTimerDuration)
                                {
                                    haveTimerToSchedule = true;
                                    nextTimerDuration = timer._dueTime;
                                }

                                // Validate that the repeating timer is still on the right list.  It's likely that
                                // it started in the long list and was moved to the short list at some point, so
                                // we now want to move it back to the long list if that's where it belongs. Note that
                                // if we're currently processing the short list and move it to the long list, we may
                                // end up revisiting it again if we also enumerate the long list, but we will have already
                                // updated the due time appropriately so that we won't fire it again (it's also possible
                                // but rare that we could be moving a timer from the long list to the short list here,
                                // if the initial due time was set to be long but the timer then had a short period).
                                bool targetShortList = (nowTicks + timer._dueTime) - _currentAbsoluteThreshold <= 0;
                                if (timer._short != targetShortList)
                                {
                                    MoveTimerToCorrectList(timer, targetShortList);
                                }
                            }
                            else
                            {
                                // Not repeating; remove it from the queue
                                DeleteTimer(timer);
                            }

                            // If this is the first timer, we'll fire it on this thread (after processing
                            // all others). Otherwise, queue it to the ThreadPool.
                            if (timerToFireOnThisThread == null)
                            {
                                timerToFireOnThisThread = timer;
                            }
                            else
                            {
                                ThreadPool.UnsafeQueueUserWorkItemInternal(timer, preferLocal: false);
                            }
                        }
                        else
                        {
                            // This timer isn't ready to fire.  Update the next time the native timer fires if necessary,
                            // and move this timer to the short list if its remaining time is now at or under the threshold.

                            if (remaining < nextTimerDuration)
                            {
                                haveTimerToSchedule = true;
                                nextTimerDuration = (uint)remaining;
                            }

                            if (!timer._short && remaining <= ShortTimersThresholdMilliseconds)
                            {
                                MoveTimerToCorrectList(timer, shortList: true);
                            }
                        }

                        timer = next;
                    }

                    // Switch to process the long list if necessary.
                    if (listNum == 0)
                    {
                        // Determine how much time remains between now and the current threshold.  If time remains,
                        // we can skip processing the long list.  We use > rather than >= because, although we
                        // know that if remaining == 0 no timers in the long list will need to be fired, we
                        // don't know without looking at them when we'll need to call FireNextTimers again.  We
                        // could in that case just set the next firing to 1, but we may as well just iterate the
                        // long list now; otherwise, most timers created in the interim would end up in the long
                        // list and we'd likely end up paying for another invocation of FireNextTimers that could
                        // have been delayed longer (to whatever is the current minimum in the long list).
                        long remaining = _currentAbsoluteThreshold - nowTicks;
                        if (remaining > 0)
                        {
                            if (_shortTimers == null && _longTimers != null)
                            {
                                // We don't have any short timers left and we haven't examined the long list,
                                // which means we likely don't have an accurate nextTimerDuration.
                                // But we do know that nothing in the long list will be firing before or at _currentAbsoluteThreshold,
                                // so we can just set nextTimerDuration to the difference between then and now.
                                nextTimerDuration = (uint)remaining + 1;
                                haveTimerToSchedule = true;
                            }
                            break;
                        }

                        // Switch to processing the long list.
                        timer = _longTimers;

                        // Now that we're going to process the long list, update the current threshold.
                        _currentAbsoluteThreshold = nowTicks + ShortTimersThresholdMilliseconds;
                    }
                }

                // If we still have scheduled timers, update the timer to ensure it fires
                // in time for the next one in line.
                if (haveTimerToSchedule)
                {
                    EnsureTimerFiresBy(nextTimerDuration);
                }
            }

            // Fire the user timer outside of the lock!
            timerToFireOnThisThread?.Fire();
        }

        #endregion

        #region Queue implementation

        public long ActiveCount { get; private set; }

        public bool UpdateTimer(TimerQueueTimer timer, uint dueTime, uint period)
        {
            long nowTicks = TickCount64;

            // The timer can be put onto the short list if it's next absolute firing time
            // is <= the current absolute threshold.
            long absoluteDueTime = nowTicks + dueTime;
            bool shouldBeShort = _currentAbsoluteThreshold - absoluteDueTime >= 0;

            if (timer._dueTime == Timeout.UnsignedInfinite)
            {
                // If the timer wasn't previously scheduled, now add it to the right list.
                timer._short = shouldBeShort;
                LinkTimer(timer);
                ++ActiveCount;
            }
            else if (timer._short != shouldBeShort)
            {
                // If the timer was previously scheduled, but this update should cause
                // it to move over the list threshold in either direction, do so.
                UnlinkTimer(timer);
                timer._short = shouldBeShort;
                LinkTimer(timer);
            }

            timer._dueTime = dueTime;
            timer._period = (period == 0) ? Timeout.UnsignedInfinite : period;
            timer._startTicks = nowTicks;
            return EnsureTimerFiresBy(dueTime);
        }

        public void MoveTimerToCorrectList(TimerQueueTimer timer, bool shortList)
        {
            Debug.Assert(timer._dueTime != Timeout.UnsignedInfinite, "Expected timer to be on a list.");
            Debug.Assert(timer._short != shortList, "Unnecessary if timer is already on the right list.");

            // Unlink it from whatever list it's on, change its list association, then re-link it.
            UnlinkTimer(timer);
            timer._short = shortList;
            LinkTimer(timer);
        }

        private void LinkTimer(TimerQueueTimer timer)
        {
            // Use timer._short to decide to which list to add.
            ref TimerQueueTimer? listHead = ref timer._short ? ref _shortTimers : ref _longTimers;
            timer._next = listHead;
            if (timer._next != null)
            {
                timer._next._prev = timer;
            }
            timer._prev = null;
            listHead = timer;
        }

        private void UnlinkTimer(TimerQueueTimer timer)
        {
            TimerQueueTimer? t = timer._next;
            if (t != null)
            {
                t._prev = timer._prev;
            }

            if (_shortTimers == timer)
            {
                Debug.Assert(timer._short);
                _shortTimers = t;
            }
            else if (_longTimers == timer)
            {
                Debug.Assert(!timer._short);
                _longTimers = t;
            }

            t = timer._prev;
            if (t != null)
            {
                t._next = timer._next;
            }

            // At this point the timer is no longer in a list, but its next and prev
            // references may still point to other nodes.  UnlinkTimer should thus be
            // followed by something that overwrites those references, either with null
            // if deleting the timer or other nodes if adding it to another list.
        }

        public void DeleteTimer(TimerQueueTimer timer)
        {
            if (timer._dueTime != Timeout.UnsignedInfinite)
            {
                --ActiveCount;
                Debug.Assert(ActiveCount >= 0);
                UnlinkTimer(timer);
                timer._prev = null;
                timer._next = null;
                timer._dueTime = Timeout.UnsignedInfinite;
                timer._period = Timeout.UnsignedInfinite;
                timer._startTicks = 0;
                timer._short = false;
            }
        }

        #endregion
    }

    // A timer in our TimerQueue.
    [DebuggerDisplay("{DisplayString,nq}")]
    [DebuggerTypeProxy(typeof(TimerDebuggerTypeProxy))]
    internal sealed class TimerQueueTimer : ITimer, IThreadPoolWorkItem
    {
        // The associated timer queue.
        private readonly TimerQueue _associatedTimerQueue;

        // All mutable fields of this class are protected by a lock on _associatedTimerQueue.
        // The first six fields are maintained by TimerQueue.

        // Links to the next and prev timers in the list.
        internal TimerQueueTimer? _next;
        internal TimerQueueTimer? _prev;

        // true if on the short list; otherwise, false.
        internal bool _short;

        // The time, according to TimerQueue.TickCount, when this timer's current interval started.
        internal long _startTicks;

        // Timeout.UnsignedInfinite if we are not going to fire.  Otherwise, the offset from _startTime when we will fire.
        internal uint _dueTime;

        // Timeout.UnsignedInfinite if we are a single-shot timer.  Otherwise, the repeat interval.
        internal uint _period;

        // Info about the user's callback
        private readonly TimerCallback _timerCallback;
        private readonly object? _state;
        private readonly ExecutionContext? _executionContext;

        // When Timer.Dispose(WaitHandle) is used, we need to signal the wait handle only
        // after all pending callbacks are complete.  We set _canceled to prevent any callbacks that
        // are already queued from running.  We track the number of callbacks currently executing in
        // _callbacksRunning.  We set _notifyWhenNoCallbacksRunning only when _callbacksRunning
        // reaches zero.  Same applies if Timer.DisposeAsync() is used, except with a Task
        // instead of with a provided WaitHandle.
        private int _callbacksRunning;
        private bool _canceled;
        internal bool _everQueued;
        private object? _notifyWhenNoCallbacksRunning; // may be either WaitHandle or Task

        internal TimerQueueTimer(TimerCallback timerCallback, object? state, TimeSpan dueTime, TimeSpan period, bool flowExecutionContext) :
            this(timerCallback, state, GetMilliseconds(dueTime), GetMilliseconds(period), flowExecutionContext)
        {
        }

        private static uint GetMilliseconds(TimeSpan time, [CallerArgumentExpression(nameof(time))] string? parameter = null)
        {
            long tm = (long)time.TotalMilliseconds;
            ArgumentOutOfRangeException.ThrowIfLessThan(tm, -1, parameter);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(tm, Timer.MaxSupportedTimeout, parameter);
            return (uint)tm;
        }

        internal TimerQueueTimer(TimerCallback timerCallback, object? state, uint dueTime, uint period, bool flowExecutionContext)
        {
            _timerCallback = timerCallback;
            _state = state;
            _dueTime = Timeout.UnsignedInfinite;
            _period = Timeout.UnsignedInfinite;
            if (flowExecutionContext)
            {
                _executionContext = ExecutionContext.Capture();
            }
            _associatedTimerQueue = TimerQueue.Instances[(uint)Thread.GetCurrentProcessorId() % TimerQueue.Instances.Length];

            // After the following statement, the timer may fire.  No more manipulation of timer state outside of
            // the lock is permitted beyond this point!
            if (dueTime != Timeout.UnsignedInfinite)
                Change(dueTime, period);
        }

        internal string DisplayString
        {
            get
            {
                string? typeName = _timerCallback.Method.DeclaringType?.FullName;
                if (typeName is not null)
                {
                    typeName += ".";
                }

                return
                    "DueTime = " + (_dueTime == Timeout.UnsignedInfinite ? "(not set)" : TimeSpan.FromMilliseconds(_dueTime)) + ", " +
                    "Period = " + (_period == Timeout.UnsignedInfinite ? "(not set)" : TimeSpan.FromMilliseconds(_period)) + ", " +
                    typeName + _timerCallback.Method.Name + "(" + (_state?.ToString() ?? "null") + ")";
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) =>
            Change(GetMilliseconds(dueTime), GetMilliseconds(period));

        internal bool Change(uint dueTime, uint period)
        {
            bool success;

            lock (_associatedTimerQueue.SharedLock)
            {
                if (_canceled)
                {
                    return false;
                }

                _period = period;

                if (dueTime == Timeout.UnsignedInfinite)
                {
                    _associatedTimerQueue.DeleteTimer(this);
                    success = true;
                }
                else
                {
                    if (FrameworkEventSource.Log.IsEnabled(EventLevel.Informational, FrameworkEventSource.Keywords.ThreadTransfer))
                        FrameworkEventSource.Log.ThreadTransferSendObj(this, 1, string.Empty, true, (int)dueTime, (int)period);
                    success = _associatedTimerQueue.UpdateTimer(this, dueTime, period);
                }
            }

            return success;
        }

        public void Dispose()
        {
            lock (_associatedTimerQueue.SharedLock)
            {
                if (!_canceled)
                {
                    _canceled = true;
                    _associatedTimerQueue.DeleteTimer(this);
                }
            }
        }

        public bool Dispose(WaitHandle toSignal)
        {
            Debug.Assert(toSignal != null);

            bool success;
            bool shouldSignal = false;

            lock (_associatedTimerQueue.SharedLock)
            {
                if (_canceled)
                {
                    success = false;
                }
                else
                {
                    _canceled = true;
                    _notifyWhenNoCallbacksRunning = toSignal;
                    _associatedTimerQueue.DeleteTimer(this);
                    shouldSignal = _callbacksRunning == 0;
                    success = true;
                }
            }

            if (shouldSignal)
                SignalNoCallbacksRunning();

            return success;
        }

        public ValueTask DisposeAsync()
        {
            lock (_associatedTimerQueue.SharedLock)
            {
                object? notifyWhenNoCallbacksRunning = _notifyWhenNoCallbacksRunning;

                // Mark the timer as canceled if it's not already.
                if (_canceled)
                {
                    if (notifyWhenNoCallbacksRunning is WaitHandle)
                    {
                        // A previous call to Close(WaitHandle) stored a WaitHandle.  We could try to deal with
                        // this case by using ThreadPool.RegisterWaitForSingleObject to create a Task that'll
                        // complete when the WaitHandle is set, but since arbitrary WaitHandle's can be supplied
                        // by the caller, it could be for an auto-reset event or similar where that caller's
                        // WaitOne on the WaitHandle could prevent this wrapper Task from completing.  We could also
                        // change the implementation to support storing multiple objects, but that's not pay-for-play,
                        // and the existing Close(WaitHandle) already discounts this as being invalid, instead just
                        // returning false if you use it multiple times. Since first calling Timer.Dispose(WaitHandle)
                        // and then calling Timer.DisposeAsync is not something anyone is likely to or should do, we
                        // simplify by just failing in that case.
                        var e = new InvalidOperationException(SR.InvalidOperation_TimerAlreadyClosed);
                        e.SetCurrentStackTrace();
                        return ValueTask.FromException(e);
                    }
                }
                else
                {
                    _canceled = true;
                    _associatedTimerQueue.DeleteTimer(this);
                }

                // We've deleted the timer, so if there are no callbacks queued or running,
                // we're done and return an already-completed value task.
                if (_callbacksRunning == 0)
                {
                    return default;
                }

                Debug.Assert(
                    notifyWhenNoCallbacksRunning == null ||
                    notifyWhenNoCallbacksRunning is Task);

                // There are callbacks queued or running, so we need to store a Task
                // that'll be used to signal the caller when all callbacks complete. Do so as long as
                // there wasn't a previous CloseAsync call that did.
                if (notifyWhenNoCallbacksRunning == null)
                {
                    var t = new Task((object?)null, TaskCreationOptions.RunContinuationsAsynchronously, true);
                    _notifyWhenNoCallbacksRunning = t;
                    return new ValueTask(t);
                }

                // A previous CloseAsync call already hooked up a task.  Just return it.
                return new ValueTask((Task)notifyWhenNoCallbacksRunning);
            }
        }

        void IThreadPoolWorkItem.Execute() => Fire(isThreadPool: true);

        internal void Fire(bool isThreadPool = false)
        {
            bool canceled;

            lock (_associatedTimerQueue.SharedLock)
            {
                canceled = _canceled;
                if (!canceled)
                    _callbacksRunning++;
            }

            if (canceled)
                return;

            CallCallback(isThreadPool);

            bool shouldSignal;
            lock (_associatedTimerQueue.SharedLock)
            {
                _callbacksRunning--;
                shouldSignal = _canceled && _callbacksRunning == 0 && _notifyWhenNoCallbacksRunning != null;
            }

            if (shouldSignal)
                SignalNoCallbacksRunning();
        }

        internal void SignalNoCallbacksRunning()
        {
            object? toSignal = _notifyWhenNoCallbacksRunning;
            Debug.Assert(toSignal is WaitHandle || toSignal is Task);

            if (toSignal is WaitHandle wh)
            {
                EventWaitHandle.Set(wh.SafeWaitHandle);
            }
            else
            {
                ((Task)toSignal).TrySetResult();
            }
        }

        internal void CallCallback(bool isThreadPool)
        {
            if (FrameworkEventSource.Log.IsEnabled(EventLevel.Informational, FrameworkEventSource.Keywords.ThreadTransfer))
                FrameworkEventSource.Log.ThreadTransferReceiveObj(this, 1, string.Empty);

            // Call directly if EC flow is suppressed
            ExecutionContext? context = _executionContext;
            if (context == null)
            {
                _timerCallback(_state);
            }
            else
            {
                if (isThreadPool)
                {
                    ExecutionContext.RunFromThreadPoolDispatchLoop(Thread.CurrentThread, context, s_callCallbackInContext, this);
                }
                else
                {
                    ExecutionContext.RunInternal(context, s_callCallbackInContext, this);
                }
            }
        }

        private static readonly ContextCallback s_callCallbackInContext = static state =>
        {
            Debug.Assert(state is TimerQueueTimer);
            var t = (TimerQueueTimer)state;
            t._timerCallback(t._state);
        };

        internal sealed class TimerDebuggerTypeProxy
        {
            private readonly TimerQueueTimer _timer;

            public TimerDebuggerTypeProxy(Timer timer) => _timer = timer._timer._timer;
            public TimerDebuggerTypeProxy(TimerQueueTimer timer) => _timer = timer;

            public DateTime? EstimatedNextTimeUtc
            {
                get
                {
                    if (_timer._dueTime != Timeout.UnsignedInfinite)
                    {
                        // In TimerQueue's static ctor, we snap a tick count and the current time, as a way of being
                        // able to translate from tick counts to times.  This is only approximate, for a variety of
                        // reasons (e.g. drift, clock changes, etc.), but when dump debugging we are unable to use
                        // TickCount in a meaningful way, so this at least provides a reasonable approximation.
                        long msOffset = _timer._startTicks - TimerQueue.s_tickCountToTimeMap.TickCount + _timer._dueTime;
                        return (TimerQueue.s_tickCountToTimeMap.Time + TimeSpan.FromMilliseconds(msOffset));
                    }

                    return null;
                }
            }

            public TimeSpan? DueTime => _timer._dueTime == Timeout.UnsignedInfinite ? null : TimeSpan.FromMilliseconds(_timer._dueTime);

            public TimeSpan? Period => _timer._period == Timeout.UnsignedInfinite ? null : TimeSpan.FromMilliseconds(_timer._period);

            public TimerCallback Callback => _timer._timerCallback;

            public object? State => _timer._state;
        }
    }

    // TimerHolder serves as an intermediary between Timer and TimerQueueTimer, releasing the TimerQueueTimer
    // if the Timer is collected.
    // This is necessary because Timer itself cannot use its finalizer for this purpose.  If it did,
    // then users could control timer lifetimes using GC.SuppressFinalize/ReRegisterForFinalize.
    // You might ask, wouldn't that be a good thing?  Maybe (though it would be even better to offer this
    // via first-class APIs), but Timer has never offered this, and adding it now would be a breaking
    // change, because any code that happened to be suppressing finalization of Timer objects would now
    // unwittingly be changing the lifetime of those timers.
    internal sealed class TimerHolder
    {
        internal readonly TimerQueueTimer _timer;

        public TimerHolder(TimerQueueTimer timer)
        {
            _timer = timer;
        }

        ~TimerHolder()
        {
            _timer.Dispose();
        }

        public void Dispose()
        {
            _timer.Dispose();
            GC.SuppressFinalize(this);
        }

        public bool Dispose(WaitHandle notifyObject)
        {
            bool result = _timer.Dispose(notifyObject);
            GC.SuppressFinalize(this);
            return result;
        }

        public ValueTask DisposeAsync()
        {
            ValueTask result = _timer.DisposeAsync();
            GC.SuppressFinalize(this);
            return result;
        }
    }

    [DebuggerDisplay("{DisplayString,nq}")]
    [DebuggerTypeProxy(typeof(TimerQueueTimer.TimerDebuggerTypeProxy))]
    public sealed class Timer : MarshalByRefObject, IDisposable, IAsyncDisposable, ITimer
    {
        internal const uint MaxSupportedTimeout = 0xfffffffe;

        internal TimerHolder _timer;

        public Timer(TimerCallback callback,
                     object? state,
                     int dueTime,
                     int period) :
                     this(callback, state, dueTime, period, flowExecutionContext: true)
        {
        }

        internal Timer(TimerCallback callback,
                       object? state,
                       int dueTime,
                       int period,
                       bool flowExecutionContext)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(dueTime, -1);
            ArgumentOutOfRangeException.ThrowIfLessThan(period, -1);

            TimerSetup(callback, state, (uint)dueTime, (uint)period, flowExecutionContext);
        }

        public Timer(TimerCallback callback,
                     object? state,
                     TimeSpan dueTime,
                     TimeSpan period)
        {
            long dueTm = (long)dueTime.TotalMilliseconds;
            ArgumentOutOfRangeException.ThrowIfLessThan(dueTm, -1, nameof(dueTime));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(dueTm, MaxSupportedTimeout, nameof(dueTime));

            long periodTm = (long)period.TotalMilliseconds;
            ArgumentOutOfRangeException.ThrowIfLessThan(periodTm, -1, nameof(period));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(periodTm, MaxSupportedTimeout, nameof(period));

            TimerSetup(callback, state, (uint)dueTm, (uint)periodTm);
        }

        [CLSCompliant(false)]
        public Timer(TimerCallback callback,
                     object? state,
                     uint dueTime,
                     uint period)
        {
            TimerSetup(callback, state, dueTime, period);
        }

        public Timer(TimerCallback callback,
                     object? state,
                     long dueTime,
                     long period)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(dueTime, -1);
            ArgumentOutOfRangeException.ThrowIfLessThan(period, -1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(dueTime, MaxSupportedTimeout);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(period, MaxSupportedTimeout);

            TimerSetup(callback, state, (uint)dueTime, (uint)period);
        }

        public Timer(TimerCallback callback)
        {
            const uint DueTime = unchecked((uint)(-1)); // We want timer to be registered, but not activated.  Requires caller to call
            const uint Period = unchecked((uint)(-1));  // Change after a timer instance is created.  This is to avoid the potential
                                // for a timer to be fired before the returned value is assigned to the variable,
                                // potentially causing the callback to reference a bogus value (if passing the timer to the callback).

            TimerSetup(callback, this, DueTime, Period);
        }

        [MemberNotNull(nameof(_timer))]
        private void TimerSetup(TimerCallback callback,
                                object? state,
                                uint dueTime,
                                uint period,
                                bool flowExecutionContext = true)
        {
            ArgumentNullException.ThrowIfNull(callback);

            _timer = new TimerHolder(new TimerQueueTimer(callback, state, dueTime, period, flowExecutionContext));
        }

        public bool Change(int dueTime, int period)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(dueTime, -1);
            ArgumentOutOfRangeException.ThrowIfLessThan(period, -1);

            return _timer._timer.Change((uint)dueTime, (uint)period);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) =>
            _timer._timer.Change(dueTime, period);

        [CLSCompliant(false)]
        public bool Change(uint dueTime, uint period)
        {
            return _timer._timer.Change(dueTime, period);
        }

        public bool Change(long dueTime, long period)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(dueTime, -1);
            ArgumentOutOfRangeException.ThrowIfLessThan(period, -1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(dueTime, MaxSupportedTimeout);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(period, MaxSupportedTimeout);

            return _timer._timer.Change((uint)dueTime, (uint)period);
        }

        /// <summary>
        /// Gets the number of timers that are currently active. An active timer is registered to tick at some point in the
        /// future, and has not yet been canceled.
        /// </summary>
        public static long ActiveCount
        {
            get
            {
                long count = 0;
                foreach (TimerQueue queue in TimerQueue.Instances)
                {
                    lock (queue.SharedLock)
                    {
                        count += queue.ActiveCount;
                    }
                }
                return count;
            }
        }

        public bool Dispose(WaitHandle notifyObject)
        {
            ArgumentNullException.ThrowIfNull(notifyObject);

            return _timer.Dispose(notifyObject);
        }

        public void Dispose()
        {
            _timer.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            return _timer.DisposeAsync();
        }

        private string DisplayString => _timer._timer.DisplayString;
    }
}
