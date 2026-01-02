using System;

namespace GenericLauncher.Auth;

public enum XstsFailureReason
{
    Unknown,
    XboxAccountMissing,
    XboxAccountBanned,
    XboxAccountNotAvailable,
    AgeVerificationRequired,
}

public sealed class XstsException : Exception
{
    public XstsFailureReason Reason { get; }
    public long Code { get; }

    public XstsException(XstsFailureReason reason, long code) : base()
    {
        Reason = reason;
        Code = code;
    }
}
