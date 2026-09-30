using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sidey.Core.Abstractions;
using Sidey.Core.Domain;
using Sidey.Core.Localization;
using Sidey.Presentation.Services;

namespace Sidey.Presentation.ViewModels;

public sealed partial class ComposerViewModel : ObservableObject, IDisposable
{
    private DelayedAction? _autoClose;
    private string _draft = string.Empty;
    private bool _disposed;
    private bool _restoringDraft;
    private readonly Func<Guid, string, Task>? _send;
    private readonly bool _autoCloseAfterSend;
    private readonly Dictionary<Guid, string> _drafts = [];
    private readonly Dictionary<Guid, string> _errors = [];
    private readonly HashSet<Guid> _notReadyErrors = [];
    private Guid? _roomId;
    private bool _canCompose = true;
    private bool _isConnectionReady = true;
    private long _interactionGeneration;

    public ComposerViewModel(Func<Guid, string, Task>? send = null, bool autoCloseAfterSend = true)
    {
        _send = send;
        _autoCloseAfterSend = autoCloseAfterSend;
    }

    public Guid? RoomId => _roomId;

    public bool CanCompose => _canCompose && !_disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorMessage { get; set; } = string.Empty;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool IsConnectionRecovering => CanCompose && !_isConnectionReady;

    public string ConnectionMessage => IsConnectionRecovering
        ? I18n.Get("connection.notice.reconnecting") : string.Empty;

    public void ApplyRoom(Guid? roomId, bool canCompose, bool isConnectionReady = true)
    {
        if (_disposed)
            return;
        if (_roomId != roomId)
        {
            OnHidden();
            if (_roomId is { } previous)
                _drafts[previous] = Draft;
            _roomId = roomId;
            RestoreDraft(roomId is { } next ? _drafts.GetValueOrDefault(next, string.Empty) : string.Empty);
            ErrorMessage = roomId is { } current ? _errors.GetValueOrDefault(current, string.Empty) : string.Empty;
        }
        if (_canCompose && !canCompose)
            OnHidden();
        _canCompose = canCompose;
        _isConnectionReady = isConnectionReady;
        if (isConnectionReady && roomId is { } readyRoom && _notReadyErrors.Remove(readyRoom))
        {
            _errors.Remove(readyRoom);
            ErrorMessage = string.Empty;
        }
        OnPropertyChanged(nameof(CanCompose));
        OnPropertyChanged(nameof(IsConnectionRecovering));
        OnPropertyChanged(nameof(ConnectionMessage));
        SendCommand.NotifyCanExecuteChanged();
    }

    public string Draft
    {
        get => _draft;
        set
        {
            if (!MessageValidator.IsValidDraft(value))
            {
                OnPropertyChanged();
                return;
            }

            if (!SetProperty(ref _draft, value))
            {
                return;
            }

            SendCommand.NotifyCanExecuteChanged();
            CancelAutoClose();
            _interactionGeneration++;
            if (!_restoringDraft)
                TypingChanged?.Invoke(!string.IsNullOrWhiteSpace(MessageValidator.Normalize(value)));
        }
    }

    public event Action? CloseRequested;

    public event Action<string>? SendRequested;

    public event Action<bool>? TypingChanged;

    public bool CanAddLine =>
        Draft.Count(character => character == '\n') + 1 < MessageValidator.MaximumLines;

    public void OnShown()
    {
        _interactionGeneration++;
        CancelAutoClose();
    }

    public void OnHidden()
    {
        _interactionGeneration++;
        CancelAutoClose();
        TypingChanged?.Invoke(false);
    }

    public void RestoreDraft(string body)
    {
        _restoringDraft = true;
        try
        { Draft = MessageValidator.IsValidDraft(body) ? body : string.Empty; }
        finally { _restoringDraft = false; }
    }

    [RelayCommand(CanExecute = nameof(CanSend), AllowConcurrentExecutions = false)]
    private async Task SendAsync()
    {
        string body = MessageValidator.Normalize(Draft);
        if (!CanSend())
        {
            SendCommand.NotifyCanExecuteChanged();
            return;
        }

        Guid? roomId = _roomId;
        RestoreDraft(string.Empty);
        ErrorMessage = string.Empty;
        if (roomId is { } clearedRoom)
        {
            _errors.Remove(clearedRoom);
            _notReadyErrors.Remove(clearedRoom);
        }
        TypingChanged?.Invoke(false);
        long generation = _interactionGeneration;
        try
        {
            if (_send is not null && roomId is { } target)
                await _send(target, body);
            else
                SendRequested?.Invoke(body);
            if (!_disposed && _autoCloseAfterSend && _roomId == roomId && Draft.Length == 0
                && generation == _interactionGeneration)
                ScheduleAutoClose();
        }
        catch (Exception exception)
        {
            if (_disposed)
                return;
            string error = exception is ChatNotReadyException
                ? exception.Message : I18n.Format("message.send.failed", exception.Message);
            if (roomId is { } failedRoom)
            {
                _errors[failedRoom] = error;
                if (exception is ChatNotReadyException)
                    _notReadyErrors.Add(failedRoom);
                else
                    _notReadyErrors.Remove(failedRoom);
            }
            if (_roomId == roomId)
            {
                if (Draft.Length == 0)
                    RestoreDraft(body);
                ErrorMessage = error;
            }
            else if (roomId is { } originalRoom && string.IsNullOrEmpty(_drafts.GetValueOrDefault(originalRoom)))
                _drafts[originalRoom] = body;
        }
    }

    [RelayCommand]
    private void Close()
    {
        TypingChanged?.Invoke(false);
        CloseRequested?.Invoke();
    }

    private bool CanSend() => CanCompose && _isConnectionReady && (_send is null || _roomId is not null)
        && MessageValidator.IsValid(MessageValidator.Normalize(Draft));

    private void ScheduleAutoClose()
    {
        CancelAutoClose();
        _autoClose = DelayedAction.Start(
            TimeSpan.FromSeconds(5),
            () => CloseRequested?.Invoke());
    }

    private void CancelAutoClose()
    {
        _autoClose?.Cancel();
        _autoClose = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TypingChanged?.Invoke(false);
        CancelAutoClose();
    }
}
