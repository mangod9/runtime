// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;

namespace System.Security.Cryptography.Pkcs
{
#if BUILDING_PKCS
    public
#else
    #pragma warning disable CA1510, CA1512
    internal
#endif
    class Pkcs9AttributeObject : AsnEncodedData
    {
        //
        // Constructors.
        //

        public Pkcs9AttributeObject()
            : base()
        {
        }

        public Pkcs9AttributeObject(string oid, byte[] encodedData)
            : this(new AsnEncodedData(oid, encodedData))
        {
        }

        public Pkcs9AttributeObject(Oid oid, byte[] encodedData)
            : this(new AsnEncodedData(oid, encodedData))
        {
        }

        public Pkcs9AttributeObject(AsnEncodedData asnEncodedData)
            : base(asnEncodedData)
        {
            if (asnEncodedData.Oid == null)
                throw new ArgumentException(SR.Format(SR.Arg_EmptyOrNullString_Named, "asnEncodedData.Oid"), nameof(asnEncodedData));
            string? szOid = base.Oid!.Value;
            if (szOid == null)
                throw new ArgumentException(SR.Format(SR.Arg_EmptyOrNullString_Named, "oid.Value"), nameof(asnEncodedData));
            if (szOid.Length == 0)
                throw new ArgumentException(SR.Format(SR.Arg_EmptyOrNullString_Named, "oid.Value"), nameof(asnEncodedData));
        }

        internal Pkcs9AttributeObject(Oid oid, ReadOnlySpan<byte> encodedData)
#if NET
            : this(new AsnEncodedData(oid, encodedData))
#else
            : this(new AsnEncodedData(oid, encodedData.ToArray()))
#endif
        {
        }

        internal Pkcs9AttributeObject(Oid oid)
        {
            base.Oid = oid;
        }

        //
        // Public properties.
        //

        public new Oid? Oid
        {
            get
            {
                return base.Oid;
            }
        }

        public override void CopyFrom(AsnEncodedData asnEncodedData)
        {
            ArgumentNullException.ThrowIfNull(asnEncodedData);

            if (!(asnEncodedData is Pkcs9AttributeObject))
                throw new ArgumentException(SR.Cryptography_Pkcs9_AttributeMismatch);

            base.CopyFrom(asnEncodedData);
        }
    }
}
