namespace HcmIdentityProvisioning.Domain.Common;

public readonly struct Result<TValue, TError>
{
    public bool IsSuccess { get; }
    public TValue Value { get; }
    public TError Error { get; }

    private Result(TValue value)
    {
        IsSuccess = true;
        Value = value;
        Error = default!;
    }

    private Result(TError error)
    {
        IsSuccess = false;
        Value = default!;
        Error = error;
    }

    public static Result<TValue, TError> Success(TValue value) => new(value);
    public static Result<TValue, TError> Failure(TError error) => new(error);
}
