namespace MarkUp.Web;

public enum ToastKind
{
    Success,
    Error,
    Warning,
    Info
}

public sealed class ToastItem
{
    public Guid Id { get; } = Guid.NewGuid();
    public ToastKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
}

/// <summary>Short pop-up messages in the corner of the screen (saved, cancelled, failed...).</summary>
public sealed class ToastService
{
    private readonly List<ToastItem> _items = new();

    public event Action? Changed;

    public IReadOnlyList<ToastItem> Items => _items;

    public void Success(string message, string title = "Done") => Show(ToastKind.Success, title, message, 4000);

    public void Error(string message, string title = "Something went wrong") => Show(ToastKind.Error, title, message, 7000);

    public void Warning(string message, string title = "Please note") => Show(ToastKind.Warning, title, message, 6000);

    public void Info(string message, string title = "") => Show(ToastKind.Info, title, message, 4500);

    public void Show(ToastKind kind, string title, string message, int milliseconds)
    {
        var item = new ToastItem { Kind = kind, Title = title, Message = message };
        _items.Add(item);
        if (_items.Count > 4) _items.RemoveAt(0);
        Changed?.Invoke();
        _ = AutoDismissAsync(item.Id, milliseconds);
    }

    public void Dismiss(Guid id)
    {
        if (_items.RemoveAll(t => t.Id == id) > 0) Changed?.Invoke();
    }

    private async Task AutoDismissAsync(Guid id, int milliseconds)
    {
        await Task.Delay(milliseconds);
        Dismiss(id);
    }
}

public sealed class ConfirmRequest
{
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    public string ConfirmText { get; init; } = "Confirm";
    public string CancelText { get; init; } = "Cancel";
    public bool Danger { get; init; }
}

/// <summary>A styled "Are you sure?" dialog, used instead of the browser's confirm box.</summary>
public sealed class ConfirmService
{
    private TaskCompletionSource<bool>? _pending;

    public event Action? Changed;

    public ConfirmRequest? Current { get; private set; }

    public Task<bool> AskAsync(string title, string message, string confirmText = "Confirm", bool danger = false, string cancelText = "Cancel")
    {
        _pending?.TrySetResult(false);
        _pending = new TaskCompletionSource<bool>();
        Current = new ConfirmRequest
        {
            Title = title,
            Message = message,
            ConfirmText = confirmText,
            CancelText = cancelText,
            Danger = danger
        };
        Changed?.Invoke();
        return _pending.Task;
    }

    public void Answer(bool result)
    {
        var pending = _pending;
        _pending = null;
        Current = null;
        Changed?.Invoke();
        pending?.TrySetResult(result);
    }
}