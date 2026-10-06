namespace HcmIdentityProvisioning.Domain.Common;

public readonly struct Result<TValue, TError>
{
    private readonly TValue _value;

    public bool IsSuccess { get; }
    public TValue Value => !IsSuccess 
        ? throw new InvalidOperationException("Cannot access Value on a failed Result.") 
        : _value;
    public TError Error { get; }

    private Result(TValue value)
    {
        IsSuccess = true;
        _value = value;
        Error = default!;
    }

    private Result(TError error)
    {
        IsSuccess = false;
        _value = default!;
        Error = error;
    }

    public static Result<TValue, TError> Success(TValue value) => new(value);
    public static Result<TValue, TError> Failure(TError error) => new(error);
}
