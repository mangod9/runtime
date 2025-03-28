// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Formats.Asn1;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.Asn1;
using Internal.Cryptography;

namespace System.Security.Cryptography
{
    internal static partial class RSAImplementation
    {
        public sealed partial class RSAAndroid : RSA, IRuntimeAlgorithm
        {
            private const int BitsPerByte = 8;

            private Lazy<SafeRsaHandle>? _key;

            public RSAAndroid()
                : this(2048)
            {
            }

            public RSAAndroid(int keySize)
            {
                base.KeySize = keySize;
                _key = new Lazy<SafeRsaHandle>(GenerateKey);
            }

            internal RSAAndroid(SafeRsaHandle key)
            {
                _key = new Lazy<SafeRsaHandle>(key.DuplicateHandle());
                SetKeySizeFromHandle(key);
            }

            public override int KeySize
            {
                set
                {
                    if (KeySize == value)
                    {
                        return;
                    }

                    // Set the KeySize before FreeKey so that an invalid value doesn't throw away the key
                    base.KeySize = value;

                    ThrowIfDisposed();
                    FreeKey();
                    _key = new Lazy<SafeRsaHandle>(GenerateKey);
                }
            }

            private void SetKeySizeFromHandle(SafeRsaHandle key)
            {
                int keySize = BitsPerByte * Interop.AndroidCrypto.RsaSize(key);

                // In the event that a key was loaded via ImportParameters or an IntPtr/SafeHandle
                // it could be outside of the bounds that we currently represent as "legal key sizes".
                // Since that is our view into the underlying component it can be detached from the
                // component's understanding.  If it said it has opened a key, and this is the size, trust it.
                KeySizeValue = keySize;
            }

            public override KeySizes[] LegalKeySizes
            {
                get
                {
                    return new[] { new KeySizes(512, 16384, 8) };
                }
            }

            public override byte[] Decrypt(byte[] data, RSAEncryptionPadding padding)
            {
                ArgumentNullException.ThrowIfNull(data);
                ArgumentNullException.ThrowIfNull(padding);

                Interop.AndroidCrypto.RsaPadding rsaPadding = GetInteropPadding(padding);
                SafeRsaHandle key = GetKey();

                int rsaSize = Interop.AndroidCrypto.RsaSize(key);
                Span<byte> destination = default;
                byte[] buf = CryptoPool.Rent(rsaSize);

                try
                {
                    destination = new Span<byte>(buf, 0, rsaSize);

                    if (!TryDecrypt(key, data, destination, rsaPadding, out int bytesWritten))
                    {
                        Debug.Fail($"{nameof(TryDecrypt)} should not return false for RSA_size buffer");
                        throw new CryptographicException();
                    }

                    return destination.Slice(0, bytesWritten).ToArray();
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(destination);
                    CryptoPool.Return(buf, clearSize: 0);
                }
            }

            public override bool TryDecrypt(
                ReadOnlySpan<byte> data,
                Span<byte> destination,
                RSAEncryptionPadding padding,
                out int bytesWritten)
            {
                ArgumentNullException.ThrowIfNull(padding);

                Interop.AndroidCrypto.RsaPadding rsaPadding = GetInteropPadding(padding);
                SafeRsaHandle key = GetKey();

                int keySizeBytes = Interop.AndroidCrypto.RsaSize(key);

                // Android does not take a length value for the destination, so it can write out of bounds.
                // To prevent the OOB write, decrypt into a temporary buffer.
                if (destination.Length < keySizeBytes)
                {
                    scoped Span<byte> tmp;
                    byte[]? rent = null;

                    // RSA up through 4096 stackalloc
                    if ((uint)keySizeBytes <= 512)
                    {
                        tmp = stackalloc byte[keySizeBytes];
                    }
                    else
                    {
                        rent = ArrayPool<byte>.Shared.Rent(keySizeBytes);
                        tmp = rent;
                    }

                    bool ret = TryDecrypt(key, data, tmp, rsaPadding, out bytesWritten);

                    if (ret)
                    {
                        tmp = tmp.Slice(0, bytesWritten);

                        if (bytesWritten > destination.Length)
                        {
                            ret = false;
                            bytesWritten = 0;
                        }
                        else
                        {
                            tmp.CopyTo(destination);
                        }

                        CryptographicOperations.ZeroMemory(tmp);
                    }

                    if (rent != null)
                    {
                        // Already cleared
                        ArrayPool<byte>.Shared.Return(rent);
                    }

                    return ret;
                }

                return TryDecrypt(key, data, destination, rsaPadding, out bytesWritten);
            }

            private static bool TryDecrypt(
                SafeRsaHandle key,
                ReadOnlySpan<byte> data,
                Span<byte> destination,
                Interop.AndroidCrypto.RsaPadding rsaPadding,
                out int bytesWritten)
            {
                // Caller should have already checked this.
                Debug.Assert(!key.IsInvalid);

                int rsaSize = Interop.AndroidCrypto.RsaSize(key);

                if (data.Length != rsaSize)
                {
                    throw new CryptographicException(SR.Cryptography_RSA_DecryptWrongSize);
                }

                if (destination.Length < rsaSize)
                {
                    bytesWritten = 0;
                    return false;
                }

                int returnValue = Interop.AndroidCrypto.RsaPrivateDecrypt(data.Length, data, destination, key, rsaPadding);
                CheckReturn(returnValue);
                bytesWritten = returnValue;
                return true;
            }

            public override byte[] Encrypt(byte[] data, RSAEncryptionPadding padding)
            {
                ArgumentNullException.ThrowIfNull(data);
                ArgumentNullException.ThrowIfNull(padding);

                Interop.AndroidCrypto.RsaPadding rsaPadding = GetInteropPadding(padding);
                SafeRsaHandle key = GetKey();

                byte[] buf = new byte[Interop.AndroidCrypto.RsaSize(key)];

                bool encrypted = TryEncrypt(
                    key,
                    data,
                    buf,
                    rsaPadding,
                    out int bytesWritten);

                if (!encrypted || bytesWritten != buf.Length)
                {
                    Debug.Fail($"TryEncrypt behaved unexpectedly: {nameof(encrypted)}=={encrypted}, {nameof(bytesWritten)}=={bytesWritten}, {nameof(buf.Length)}=={buf.Length}");
                    throw new CryptographicException();
                }

                return buf;
            }

            public override bool TryEncrypt(ReadOnlySpan<byte> data, Span<byte> destination, RSAEncryptionPadding padding, out int bytesWritten)
            {
                ArgumentNullException.ThrowIfNull(padding);

                Interop.AndroidCrypto.RsaPadding rsaPadding = GetInteropPadding(padding);
                SafeRsaHandle key = GetKey();

                return TryEncrypt(key, data, destination, rsaPadding, out bytesWritten);
            }

            private static bool TryEncrypt(
                SafeRsaHandle key,
                ReadOnlySpan<byte> data,
                Span<byte> destination,
                Interop.AndroidCrypto.RsaPadding rsaPadding,
                out int bytesWritten)
            {
                int rsaSize = Interop.AndroidCrypto.RsaSize(key);

                if (destination.Length < rsaSize)
                {
                    bytesWritten = 0;
                    return false;
                }

                int returnValue = Interop.AndroidCrypto.RsaPublicEncrypt(data.Length, data, destination, key, rsaPadding);
                CheckReturn(returnValue);
                bytesWritten = returnValue;
                Debug.Assert(returnValue == rsaSize, $"{returnValue} != {rsaSize}");
                return true;

            }

            private static Interop.AndroidCrypto.RsaPadding GetInteropPadding(RSAEncryptionPadding padding)
            {
                if (padding == RSAEncryptionPadding.Pkcs1)
                {
                    return Interop.AndroidCrypto.RsaPadding.Pkcs1;
                }

                if (padding == RSAEncryptionPadding.OaepSHA1)
                {
                    return Interop.AndroidCrypto.RsaPadding.OaepSHA1;
                }

                if (padding == RSAEncryptionPadding.OaepSHA256)
                {
                    return Interop.AndroidCrypto.RsaPadding.OaepSHA256;
                }

                if (padding == RSAEncryptionPadding.OaepSHA384)
                {
                    return Interop.AndroidCrypto.RsaPadding.OaepSHA384;
                }

                if (padding == RSAEncryptionPadding.OaepSHA512)
                {
                    return Interop.AndroidCrypto.RsaPadding.OaepSHA512;
                }

                throw PaddingModeNotSupported();
            }

            public override RSAParameters ExportParameters(bool includePrivateParameters)
            {
                // It's entirely possible that this line will cause the key to be generated in the first place.
                SafeRsaHandle key = GetKey();

                RSAParameters rsaParameters = Interop.AndroidCrypto.ExportRsaParameters(key, includePrivateParameters);
                bool hasPrivateKey = rsaParameters.D != null;

                if (hasPrivateKey != includePrivateParameters || !HasConsistentPrivateKey(ref rsaParameters))
                {
                    throw new CryptographicException(SR.Cryptography_CSP_NoPrivateKey);
                }

                return rsaParameters;
            }

            public override void ImportParameters(RSAParameters parameters)
            {
                ValidateParameters(ref parameters);
                ThrowIfDisposed();

                if (parameters.Exponent == null || !parameters.Modulus.AsSpan().ContainsAnyExcept((byte)0))
                {
                    throw new CryptographicException(SR.Cryptography_InvalidRsaParameters);
                }

                Debug.Assert(parameters.Modulus is not null);

                // Check that either all parameters are not null or all are null, if a subset were set, then the parameters are invalid.
                // If the parameters are all not null, verify the integrity of their lengths.
                if (parameters.D == null)
                {
                    if (parameters.P != null ||
                        parameters.DP != null ||
                        parameters.Q != null ||
                        parameters.DQ != null ||
                        parameters.InverseQ != null)
                    {
                        throw new CryptographicException(SR.Cryptography_InvalidRsaParameters);
                    }
                }
                else
                {
                    if (parameters.P == null ||
                        parameters.DP == null ||
                        parameters.Q == null ||
                        parameters.DQ == null ||
                        parameters.InverseQ == null)
                    {
                        throw new CryptographicException(SR.Cryptography_InvalidRsaParameters);
                    }

                    // Half, rounded up.
                    int halfModulusLength = (parameters.Modulus.Length + 1) / 2;

                    // Matching the .NET Framework RSACryptoServiceProvider behavior, as that's the .NET de facto standard
                    if (parameters.D.Length != parameters.Modulus.Length ||
                        parameters.P.Length != halfModulusLength ||
                        parameters.Q.Length != halfModulusLength ||
                        parameters.DP.Length != halfModulusLength ||
                        parameters.DQ.Length != halfModulusLength ||
                        parameters.InverseQ.Length != halfModulusLength)
                    {
                        throw new CryptographicException(SR.Cryptography_InvalidRsaParameters);
                    }
                }

                SafeRsaHandle key = Interop.AndroidCrypto.RsaCreate();
                bool imported = false;

                if (key is null || key.IsInvalid)
                {
                    key?.Dispose();
                    throw new CryptographicException();
                }

                try
                {
                    if (!Interop.AndroidCrypto.SetRsaParameters(
                        key,
                        parameters.Modulus,
                        parameters.Modulus != null ? parameters.Modulus.Length : 0,
                        parameters.Exponent,
                        parameters.Exponent != null ? parameters.Exponent.Length : 0,
                        parameters.D,
                        parameters.D != null ? parameters.D.Length : 0,
                        parameters.P,
                        parameters.P != null ? parameters.P.Length : 0,
                        parameters.DP,
                        parameters.DP != null ? parameters.DP.Length : 0,
                        parameters.Q,
                        parameters.Q != null ? parameters.Q.Length : 0,
                        parameters.DQ,
                        parameters.DQ != null ? parameters.DQ.Length : 0,
                        parameters.InverseQ,
                        parameters.InverseQ != null ? parameters.InverseQ.Length : 0))
                    {
                        throw new CryptographicException();
                    }

                    imported = true;
                }
                finally
                {
                    if (!imported)
                    {
                        key.Dispose();
                    }
                }

                FreeKey();
                _key = new Lazy<SafeRsaHandle>(key);
                SetKeySizeFromHandle(key);
            }

            public override unsafe void ImportRSAPublicKey(ReadOnlySpan<byte> source, out int bytesRead)
            {
                ThrowIfDisposed();

                fixed (byte* ptr = &MemoryMarshal.GetReference(source))
                {
                    using (MemoryManager<byte> manager = new PointerMemoryManager<byte>(ptr, source.Length))
                    {
                        ReadOnlyMemory<byte> subjectPublicKey;
                        try
                        {
                            AsnReader reader = new AsnReader(manager.Memory, AsnEncodingRules.BER);
                            subjectPublicKey = reader.PeekEncodedValue();
                        }
                        catch (AsnContentException e)
                        {
                            throw new CryptographicException(SR.Cryptography_Der_Invalid_Encoding, e);
                        }

                        // Decoding the key on Android requires the encoded SubjectPublicKeyInfo,
                        // not just the SubjectPublicKey, so we construct one.
                        SubjectPublicKeyInfoAsn spki = new SubjectPublicKeyInfoAsn
                        {
                            Algorithm = new AlgorithmIdentifierAsn
                            {
                                Algorithm = Oids.Rsa,
                                Parameters = AlgorithmIdentifierAsn.ExplicitDerNull,
                            },
                            SubjectPublicKey = subjectPublicKey,
                        };

                        AsnWriter writer = new AsnWriter(AsnEncodingRules.DER);
                        spki.Encode(writer);

                        SafeRsaHandle key = writer.Encode(static (encoded) =>
                        {
                            return Interop.AndroidCrypto.DecodeRsaSubjectPublicKeyInfo(encoded);
                        });

                        if (key is null || key.IsInvalid)
                        {
                            key?.Dispose();
                            throw new CryptographicException();
                        }

                        FreeKey();
                        _key = new Lazy<SafeRsaHandle>(key);
                        SetKeySizeFromHandle(key);

                        bytesRead = subjectPublicKey.Length;
                    }
                }
            }

            public override void ImportEncryptedPkcs8PrivateKey(
                ReadOnlySpan<byte> passwordBytes,
                ReadOnlySpan<byte> source,
                out int bytesRead)
            {
                ThrowIfDisposed();
                base.ImportEncryptedPkcs8PrivateKey(passwordBytes, source, out bytesRead);
            }

            public override void ImportEncryptedPkcs8PrivateKey(
                ReadOnlySpan<char> password,
                ReadOnlySpan<byte> source,
                out int bytesRead)
            {
                ThrowIfDisposed();
                base.ImportEncryptedPkcs8PrivateKey(password, source, out bytesRead);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    FreeKey();
                    _key = null;
                }

                base.Dispose(disposing);
            }

            private void FreeKey()
            {
                if (_key != null && _key.IsValueCreated)
                {
                    SafeRsaHandle handle = _key.Value;
                    handle?.Dispose();
                }
            }

            private static void ValidateParameters(ref RSAParameters parameters)
            {
                if (parameters.Modulus == null || parameters.Exponent == null)
                    throw new CryptographicException(SR.Argument_InvalidValue);

                if (!HasConsistentPrivateKey(ref parameters))
                    throw new CryptographicException(SR.Argument_InvalidValue);
            }

            private static bool HasConsistentPrivateKey(ref RSAParameters parameters)
            {
                if (parameters.D == null)
                {
                    if (parameters.P != null ||
                        parameters.DP != null ||
                        parameters.Q != null ||
                        parameters.DQ != null ||
                        parameters.InverseQ != null)
                    {
                        return false;
                    }
                }
                else
                {
                    if (parameters.P == null ||
                        parameters.DP == null ||
                        parameters.Q == null ||
                        parameters.DQ == null ||
                        parameters.InverseQ == null)
                    {
                        return false;
                    }
                }

                return true;
            }

            [MemberNotNull(nameof(_key))]
            private void ThrowIfDisposed()
            {
                ObjectDisposedException.ThrowIf(_key is null, this);
            }

            private SafeRsaHandle GetKey()
            {
                ThrowIfDisposed();

                SafeRsaHandle key = _key.Value;

                if (key == null || key.IsInvalid)
                {
                    throw new CryptographicException(SR.Cryptography_OpenInvalidHandle);
                }

                return key;
            }

            private static void CheckReturn(int returnValue)
            {
                if (returnValue == -1)
                {
                    throw new CryptographicException();
                }
            }

            private static void CheckBoolReturn(int returnValue)
            {
                if (returnValue != 1)
                {
                    throw new CryptographicException();
                }
            }

            private SafeRsaHandle GenerateKey()
            {
                SafeRsaHandle key = Interop.AndroidCrypto.RsaCreate();
                bool generated = false;

                if (key is null || key.IsInvalid)
                {
                    key?.Dispose();
                    throw new CryptographicException();
                }

                try
                {
                    // The documentation for RSA_generate_key_ex does not say that it returns only
                    // 0 or 1, so the call marshals it back as a full Int32 and checks for a value
                    // of 1 explicitly.
                    int response = Interop.AndroidCrypto.RsaGenerateKeyEx(
                        key,
                        KeySize);

                    CheckBoolReturn(response);
                    generated = true;
                }
                finally
                {
                    if (!generated)
                    {
                        key.Dispose();
                    }
                }

                return key;
            }

            public override byte[] SignHash(byte[] hash, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding)
            {
                ArgumentNullException.ThrowIfNull(hash);
                ArgumentException.ThrowIfNullOrEmpty(hashAlgorithm.Name, nameof(hashAlgorithm));
                ArgumentNullException.ThrowIfNull(padding);

                if (!TrySignHash(
                    hash,
                    Span<byte>.Empty,
                    hashAlgorithm, padding,
                    true,
                    out _,
                    out byte[]? signature))
                {
                    Debug.Fail("TrySignHash should not return false in allocation mode");
                    throw new CryptographicException();
                }

                Debug.Assert(signature != null);
                return signature;
            }

            public override bool TrySignHash(
                ReadOnlySpan<byte> hash,
                Span<byte> destination,
                HashAlgorithmName hashAlgorithm,
                RSASignaturePadding padding,
                out int bytesWritten)
            {
                ArgumentException.ThrowIfNullOrEmpty(hashAlgorithm.Name, nameof(hashAlgorithm));
                ArgumentNullException.ThrowIfNull(padding);

                bool ret = TrySignHash(
                    hash,
                    destination,
                    hashAlgorithm,
                    padding,
                    false,
                    out bytesWritten,
                    out byte[]? alloced);

                Debug.Assert(alloced == null);
                return ret;
            }

            private bool TrySignHash(
                ReadOnlySpan<byte> hash,
                Span<byte> destination,
                HashAlgorithmName hashAlgorithm,
                RSASignaturePadding padding,
                bool allocateSignature,
                out int bytesWritten,
                out byte[]? signature)
            {
                Debug.Assert(!string.IsNullOrEmpty(hashAlgorithm.Name));
                Debug.Assert(padding != null);
                signature = null;

                if (padding != RSASignaturePadding.Pkcs1 && padding != RSASignaturePadding.Pss)
                {
                    throw PaddingModeNotSupported();
                }

                SafeRsaHandle rsa = GetKey();

                int bytesRequired = Interop.AndroidCrypto.RsaSize(rsa);

                if (allocateSignature)
                {
                    Debug.Assert(destination.Length == 0);
                    signature = new byte[bytesRequired];
                    destination = signature;
                }

                if (destination.Length < bytesRequired)
                {
                    bytesWritten = 0;
                    return false;
                }

                byte[] encodedRented = CryptoPool.Rent(bytesRequired);
                Span<byte> encodedBytes = new Span<byte>(encodedRented, 0, bytesRequired);

                if (padding.Mode == RSASignaturePaddingMode.Pkcs1)
                {
                    RsaPaddingProcessor.PadPkcs1Signature(hashAlgorithm, hash, encodedBytes);
                }
                else if (padding.Mode == RSASignaturePaddingMode.Pss)
                {
                    RsaPaddingProcessor.EncodePss(hashAlgorithm, hash, encodedBytes, KeySize);
                }
                else
                {
                    Debug.Fail("Padding mode should be checked prior to this point.");
                    throw PaddingModeNotSupported();
                }

                int ret = Interop.AndroidCrypto.RsaSignPrimitive(encodedBytes, destination, rsa);

                CryptoPool.Return(encodedRented, bytesRequired);

                CheckReturn(ret);

                Debug.Assert(
                    ret == bytesRequired,
                    $"RsaSignPrimitive returned {ret} when {bytesRequired} was expected");

                bytesWritten = ret;
                return true;
            }

            public override bool VerifyHash(
                byte[] hash,
                byte[] signature,
                HashAlgorithmName hashAlgorithm,
                RSASignaturePadding padding)
            {
                ArgumentNullException.ThrowIfNull(hash);
                ArgumentNullException.ThrowIfNull(signature);

                return VerifyHash(new ReadOnlySpan<byte>(hash), new ReadOnlySpan<byte>(signature), hashAlgorithm, padding);
            }

            public override bool VerifyHash(ReadOnlySpan<byte> hash, ReadOnlySpan<byte> signature, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding)
            {
                ArgumentException.ThrowIfNullOrEmpty(hashAlgorithm.Name, nameof(hashAlgorithm));
                ArgumentNullException.ThrowIfNull(padding);
                if (padding != RSASignaturePadding.Pkcs1 && padding != RSASignaturePadding.Pss)
                {
                    throw PaddingModeNotSupported();
                }

                SafeRsaHandle rsa = GetKey();

                int requiredBytes = Interop.AndroidCrypto.RsaSize(rsa);

                if (signature.Length != requiredBytes)
                {
                    return false;
                }

                if (hash.Length != RsaPaddingProcessor.HashLength(hashAlgorithm))
                {
                    return false;
                }

                byte[] rented = CryptoPool.Rent(requiredBytes);
                Span<byte> unwrapped = new Span<byte>(rented, 0, requiredBytes);

                try
                {
                    int ret = Interop.AndroidCrypto.RsaVerificationPrimitive(signature, unwrapped, rsa);

                    CheckReturn(ret);
                    if (ret == 0)
                    {
                        // Return value of 0 from RsaVerificationPrimitive indicates the signature could not be decrypted.
                        return false;
                    }

                    Debug.Assert(
                        ret == requiredBytes,
                        $"RsaVerificationPrimitive returned {ret} when {requiredBytes} was expected");

                    if (padding == RSASignaturePadding.Pkcs1)
                    {
                        byte[] repadRent = CryptoPool.Rent(unwrapped.Length);
                        Span<byte> repadded = repadRent.AsSpan(0, requiredBytes);
                        RsaPaddingProcessor.PadPkcs1Signature(hashAlgorithm, hash, repadded);
                        bool valid = CryptographicOperations.FixedTimeEquals(repadded, unwrapped);
                        CryptoPool.Return(repadRent, requiredBytes);
                        return valid;
                    }
                    else if (padding == RSASignaturePadding.Pss)
                    {
                        return RsaPaddingProcessor.VerifyPss(hashAlgorithm, hash, unwrapped, KeySize);
                    }
                    else
                    {
                        Debug.Fail("Padding mode should be checked prior to this point.");
                        throw PaddingModeNotSupported();
                    }
                }
                finally
                {
                    CryptoPool.Return(rented, requiredBytes);
                }

                throw PaddingModeNotSupported();
            }

            internal SafeRsaHandle DuplicateKeyHandle()
            {
                ThrowIfDisposed();
                return _key.Value.DuplicateHandle();
            }

            private static CryptographicException PaddingModeNotSupported() =>
                new CryptographicException(SR.Cryptography_InvalidPaddingMode);
        }
    }
}
