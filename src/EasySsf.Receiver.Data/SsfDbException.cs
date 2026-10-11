namespace EasySsf.Receiver.Data;

/// <summary>
/// A statement of a store failed.
/// </summary>
public class SsfDbException : Exception
{
    /// <param name="message">what failed</param>
    /// <param name="sqlState">the SQLSTATE of the failure, <see langword="null"/> if the provider gave none</param>
    /// <param name="innerException">the failure of the provider</param>
    public SsfDbException(string message, string? sqlState, Exception? innerException)
        : base(message, innerException)
    {
        SqlState = sqlState;
    }

    /// <summary>
    /// The SQLSTATE of the failure, <see langword="null"/> if the provider gave none.
    /// </summary>
    public string? SqlState { get; }
}

/// <summary>
/// An insert violated a unique constraint: the row exists already.
/// </summary>
public class SsfDbDuplicateKeyException : SsfDbException
{
    public SsfDbDuplicateKeyException(string message, string? sqlState, Exception? innerException)
        : base(message, sqlState, innerException)
    {
    }
}
