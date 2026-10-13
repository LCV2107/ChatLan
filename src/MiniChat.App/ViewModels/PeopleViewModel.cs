using System.Collections.ObjectModel;
using MiniChat.App.Infrastructure;
using MiniChat.Core.Engine;
using MiniChat.Core.Model;

namespace MiniChat.App.ViewModels;

/// <summary>Una persona en la lista. Disponibilidad (red) y relación (autorización) se muestran por separado.</summary>
public sealed class PersonItem : ObservableObject
{
    private string? _message;
    private bool _messageIsError;

    public PersonItem(PersonView v, string pairingCode)
    {
        View = v;
        PairingCode = pairingCode;
    }

    public PersonView View { get; }
    public string Fingerprint => View.Fingerprint;
    public long? ContactId => View.ContactId;
    public string Name => View.DisplayName;
    public string Machine => View.MachineName;
    public bool Online => View.Online;
    public string AvailabilityText => View.Online ? "Disponible" : "Desconectado";
    public int Unread => View.UnreadCount;
    public bool HasUnread => View.UnreadCount > 0;
    public string PairingCode { get; }
    public string ShortId => "ID " + View.Fingerprint[..4] + "-" + View.Fingerprint.Substring(4, 4);

    public bool IsBlocked => View.Blocked;
    public bool IsAuthorized => View.Relation == Relation.Authorized && !View.Blocked;
    public bool IsIncoming => View.Relation == Relation.IncomingPending && !View.Blocked;
    public bool IsOutgoing => View.Relation == Relation.OutgoingPending && !View.Blocked;
    public bool CanRequest => !View.Blocked && View.Online && View.CompatibleVersion &&
        View.Relation is Relation.None or Relation.RejectedByPeer or Relation.RejectedByMe or Relation.RevokedByPeer;
    public bool CanOpenChat => View.ContactId is not null && (IsAuthorized || View.HasHistory || IsBlocked);
    public bool IncompatibleVersion => !View.CompatibleVersion;

    public string? IdentityWarning => View.PossibleIdentityChangeOfName is { } n
        ? $"Mismo equipo que {n}, pero con otra identidad (¿reinstalación?). Requiere un emparejamiento nuevo."
        : null;
    public bool HasIdentityWarning => IdentityWarning is not null;

    public string RelationText => View.Blocked ? "Bloqueado" : View.Relation switch
    {
        Relation.Authorized => "Autorizado",
        Relation.OutgoingPending => "Solicitud enviada",
        Relation.IncomingPending => "Quiere chatear contigo",
        Relation.RejectedByMe => "Rechazaste su solicitud",
        Relation.RejectedByPeer => "Rechazó tu solicitud",
        Relation.RevokedByPeer => "Ya no te autoriza",
        _ => View.ContactId is null ? "Sin solicitud" : "Sin autorización",
    };

    /// <summary>Resultado de la última acción, mostrado junto a la persona.</summary>
    public string? Message { get => _message; set { Set(ref _message, value); OnPropertyChanged(nameof(HasMessage)); } }
    public bool MessageIsError { get => _messageIsError; set => Set(ref _messageIsError, value); }
    public bool HasMessage => !string.IsNullOrEmpty(_message);
}

public sealed class PeopleViewModel : ObservableObject
{
    private readonly ChatEngine _engine;
    private readonly MainViewModel _main;
    private readonly Dictionary<string, (string Text, bool Error)> _messages = new();
    private string _manualIp = "";
    private string? _manualResult;
    private bool _manualIsError;
    private bool _refreshQueued;

    public PeopleViewModel(ChatEngine engine, MainViewModel main)
    {
        _engine = engine;
        _main = main;
        RequestCommand = new AsyncCommand(p => RunAsync(p, i => _engine.SendChatRequestAsync(i.Fingerprint)));
        AcceptCommand = new AsyncCommand(p => RunAsync(p, i => _engine.AcceptAsync(i.ContactId!.Value)));
        RejectCommand = new AsyncCommand(p => RunAsync(p, i => _engine.RejectAsync(i.ContactId!.Value)));
        CancelCommand = new AsyncCommand(p => RunAsync(p, i => _engine.CancelRequestAsync(i.ContactId!.Value)));
        BlockCommand = new AsyncCommand(p => RunAsync(p, i => _engine.BlockAsync(i.ContactId!.Value)));
        UnblockCommand = new AsyncCommand(p => RunAsync(p, i => _engine.UnblockAsync(i.ContactId!.Value)));
        OpenCommand = new RelayCommand(p => { if (p is PersonItem { ContactId: long id }) _main.OpenChat(id); });
        ConnectIpCommand = new AsyncCommand(ConnectManualAsync);
    }

    public ObservableCollection<PersonItem> Requests { get; } = new();
    public ObservableCollection<PersonItem> Contacts { get; } = new();
    public ObservableCollection<PersonItem> Available { get; } = new();

    public bool HasRequests => Requests.Count > 0;
    public bool HasContacts => Contacts.Count > 0;
    public bool HasAvailable => Available.Count > 0;
    public int IncomingCount => Requests.Count(r => r.IsIncoming);

    public AsyncCommand RequestCommand { get; }
    public AsyncCommand AcceptCommand { get; }
    public AsyncCommand RejectCommand { get; }
    public AsyncCommand CancelCommand { get; }
    public AsyncCommand BlockCommand { get; }
    public AsyncCommand UnblockCommand { get; }
    public RelayCommand OpenCommand { get; }
    public AsyncCommand ConnectIpCommand { get; }

    public string ManualIp { get => _manualIp; set => Set(ref _manualIp, value); }
    public string? ManualResult { get => _manualResult; set { Set(ref _manualResult, value); OnPropertyChanged(nameof(HasManualResult)); } }
    public bool ManualIsError { get => _manualIsError; set => Set(ref _manualIsError, value); }
    public bool HasManualResult => !string.IsNullOrEmpty(_manualResult);

    public string NetworkHint
    {
        get
        {
            var allowed = _engine.Policy.AllowedInterfaces;
            return allowed.Count == 0
                ? "No hay ninguna red autorizada: MiniChat no puede ver ni recibir a nadie. Revise Ajustes › Redes."
                : "Buscando compañeros en " + string.Join(", ", allowed.Select(a => a.SubnetText).Concat(_engine.Policy.RemoteSubnets.Select(r => r.Text)).Distinct()) + "…";
        }
    }

    /// <summary>Al bloquear: se vacían las listas (contienen nombres descifrados).</summary>
    public void Clear()
    {
        Requests.Clear();
        Contacts.Clear();
        Available.Clear();
        _messages.Clear();
        ManualResult = null;
        OnPropertyChanged(nameof(HasRequests));
        OnPropertyChanged(nameof(HasContacts));
        OnPropertyChanged(nameof(HasAvailable));
    }

    /// <summary>Agrupa varios avisos seguidos en una sola recarga.</summary>
    public void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        _ = Task.Delay(150).ContinueWith(_ => Ui.Post(async () =>
        {
            _refreshQueued = false;
            await RefreshAsync();
        }));
    }

    public async Task RefreshAsync()
    {
        if (_engine.IsLocked) return;
        IReadOnlyList<PersonView> people;
        try { people = await _engine.GetPeopleAsync(); }
        catch (Exception ex) { _main.ReportUnexpected(ex); return; }

        var items = people.Select(p =>
        {
            var item = new PersonItem(p, _engine.PairingCodeWith(p.Fingerprint));
            if (_messages.TryGetValue(p.Fingerprint, out var m)) { item.Message = m.Text; item.MessageIsError = m.Error; }
            return item;
        }).ToList();

        Replace(Requests, items.Where(i => i.IsIncoming || i.IsOutgoing).OrderBy(i => i.IsOutgoing).ThenBy(i => i.Name));
        Replace(Contacts, items.Where(i => i.ContactId is not null && !i.IsIncoming && !i.IsOutgoing && (i.IsAuthorized || i.IsBlocked || i.View.HasHistory || i.View.Relation != Relation.None))
            .OrderByDescending(i => i.HasUnread).ThenByDescending(i => i.Online).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase));
        var shown = Requests.Concat(Contacts).Select(i => i.Fingerprint).ToHashSet();
        Replace(Available, items.Where(i => i.Online && !shown.Contains(i.Fingerprint)).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase));

        OnPropertyChanged(nameof(HasRequests));
        OnPropertyChanged(nameof(HasContacts));
        OnPropertyChanged(nameof(HasAvailable));
        OnPropertyChanged(nameof(IncomingCount));
        OnPropertyChanged(nameof(NetworkHint));
        _main.OnPeopleRefreshed(items);
    }

    private static void Replace(ObservableCollection<PersonItem> target, IEnumerable<PersonItem> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    private async Task RunAsync(object? parameter, Func<PersonItem, Task<OperationResult>> action)
    {
        if (parameter is not PersonItem item) return;
        item.Message = "…";
        item.MessageIsError = false;
        var result = await action(item);
        _messages[item.Fingerprint] = (result.Message ?? (result.Success ? "Hecho." : "No se pudo completar."), !result.Success);
        item.Message = _messages[item.Fingerprint].Text;
        item.MessageIsError = !result.Success;
        await RefreshAsync();
    }

    private async Task ConnectManualAsync()
    {
        ManualResult = "Conectando…";
        ManualIsError = false;
        var text = ManualIp.Trim();
        int? port = null;
        var colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out var p)) { port = p; text = text[..colon]; }
        var r = await _engine.ConnectManualAsync(text, port);
        ManualResult = r.Success ? r.Message + " Aparece en la lista; envíele una solicitud si aún no es contacto." : r.Message;
        ManualIsError = !r.Success;
        await RefreshAsync();
    }
}
