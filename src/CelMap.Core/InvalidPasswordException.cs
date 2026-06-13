namespace CelMap.Core;

/// <summary>Thrown when an encrypted workbook is opened with a wrong or missing password.
/// Lives in the root namespace because it's part of <see cref="IWorkbookReader"/>'s contract:
/// callers catch it to prompt for (or re-prompt for) the password.</summary>
public sealed class InvalidPasswordException : Exception
{
    public InvalidPasswordException() : base("The password is incorrect.") { }
}
