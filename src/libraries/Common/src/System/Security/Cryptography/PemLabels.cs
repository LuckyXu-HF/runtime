// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Security.Cryptography
{
    internal static class PemLabels
    {
        internal const string Pkcs8PrivateKey = "PRIVATE KEY";
        internal const string EncryptedPkcs8PrivateKey = "ENCRYPTED PRIVATE KEY";
        internal const string SpkiPublicKey = "PUBLIC KEY";
        internal const string RsaPublicKey = "RSA PUBLIC KEY";
        internal const string RsaPrivateKey = "RSA PRIVATE KEY";
        internal const string EcPrivateKey = "EC PRIVATE KEY";
        internal const string X509Certificate = "CERTIFICATE";
        internal const string Pkcs7Certificate = "PKCS7";
        internal const string X509CertificateRevocationList = "X509 CRL";
        internal const string Pkcs10CertificateRequest = "CERTIFICATE REQUEST";

        internal static ReadOnlySpan<byte> X509CertificateUtf8 => "CERTIFICATE"u8;
        internal static ReadOnlySpan<byte> Pkcs7CertificateUtf8 => "PKCS7"u8;
    }
}
