using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MiniChat.App.Infrastructure;
using MiniChat.Core.Engine;
using MiniChat.Core.Model;

namespace MiniChat.App.ViewModels;

/// <summary>Caché pequeña de miniaturas decodificadas (las imágenes se cargan bajo demanda, fuera del hilo de UI).</summary>
public static class StickerImageCache
{
    private const int Capacity = 96;
    private static readonly Dictionary<string, LinkedListNode<(string Key, ImageSource Image)>> Map = new();
    private static readonly LinkedList<(string Key, ImageSource Image)> Lru = new();

    public static void Clear()
    {
        lock (Map)
        {
            Map.Clear();
            Lru.Clear();
        }
    }

    public static ImageSource? TryGet(string key)
    {
        lock (Map)
        {
            if (!Map.TryGetValue(key, out var node)) return null;
            Lru.Remove(node);
            Lru.AddFirst(node);
            return node.Value.Image;
        }
    }

    /// <summary>
    /// Carga una imagen a partir de sus bytes ya descifrados (los stickers están cifrados en disco, así que
    /// nunca se abren por ruta). La caché vive solo en memoria y se vacía al bloquear u ocultar.
    /// </summary>
    public static async Task<ImageSource?> LoadAsync(string hash, Func<string, Task<byte[]?>> loader, int decodeWidth)
    {
        var key = hash + "|" + decodeWidth;
        if (TryGet(key) is { } cached) return cached;
        var image = await Task.Run(async () =>
        {
            try
            {
                var bytes = await loader(hash);
                if (bytes is null) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.DecodePixelWidth = decodeWidth;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();
                return (ImageSource)bmp;
            }
            catch (Exception)
            {
                return null; // imagen dañada, ausente o app bloqueada: se muestra un marcador
            }
        });
        if (image is null) return null;
        lock (Map)
        {
            if (!Map.ContainsKey(key))
            {
                Map[key] = Lru.AddFirst((key, image!));
                while (Lru.Count > Capacity)
                {
                    Map.Remove(Lru.Last!.Value.Key);
                    Lru.RemoveLast();
                }
            }
        }
        return image;
    }
}

public sealed class MessageItem : ObservableObject
{
    private readonly Func<string, Task<byte[]?>> _loader;
    private ChatMessage _m;
    private ImageSource? _image;
    private bool _loading;

    public MessageItem(ChatMessage m, Func<string, Task<byte[]?>> loader)
    {
        _m = m;
        _loader = loader;
    }

    public long Id => _m.Id;
    public bool IsOutgoing => _m.Direction == MessageDirection.Outgoing;
    public bool IsSticker => _m.Kind == MessageKind.Sticker;
    public bool IsText => _m.Kind == MessageKind.Text;
    public bool IsFile => _m.Kind == MessageKind.File;
    public string Text => _m.Text ?? "";
    public string Time => _m.LocalTime.ToLocalTime().ToString(_m.LocalTime.ToLocalTime().Date == DateTime.Today ? "HH:mm" : "dd/MM HH:mm");
    public MessageStatus Status => _m.Status;
    public string StatusText => IsOutgoing ? _m.Status switch
    {
        MessageStatus.Pending => "Pendiente",
        MessageStatus.Sending => "Enviando…",
        MessageStatus.Delivered => "Entregado",
        MessageStatus.Unconfirmed => "No confirmado",
        MessageStatus.Failed => "Fallido",
        _ => "",
    } : "";
    public bool IsProblem => IsOutgoing && _m.Status is MessageStatus.Failed or MessageStatus.Unconfirmed;
    public string? Error => _m.Error;
    public string ToolTipText => _m.Error ?? (IsOutgoing && _m.Status == MessageStatus.Delivered
        ? "Entregado: el equipo del compañero recibió y guardó el mensaje (no indica que lo haya leído)." : StatusText);

    public ImageSource? StickerImage
    {
        get
        {
            if (_image is null && IsSticker && !_loading && _m.StickerHash is { } h)
            {
                _loading = true;
                _ = LoadAsync(h);
            }
            return _image;
        }
    }

    public bool StickerMissing { get; private set; }

    private async Task LoadAsync(string hash)
    {
        _image = await StickerImageCache.LoadAsync(hash, _loader, 240);
        StickerMissing = _image is null;
        OnPropertyChanged(nameof(StickerImage));
        OnPropertyChanged(nameof(StickerMissing));
    }

    public void Update(ChatMessage m)
    {
        var file = _m.File;
        _m = m.File is null && file is not null ? m with { File = file } : m;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsProblem));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(ToolTipText));
        RaiseFile();
    }

    // ---------- Documento adjunto ----------

    public FileTransfer? File => _m.File;
    public string Uid => _m.Uid;
    public string FileName => _m.File?.Name ?? "";
    public string FileSizeText => _m.File is { } f ? MiniChat.Core.Files.FileNames.FormatSize(f.Size) : "";
    public bool IsDangerousFile => _m.File is { } f && MiniChat.Core.Files.FileNames.IsDangerous(f.Name);
    public int Progress => _m.File is { Size: > 0 } f ? (int)Math.Min(100, f.Transferred * 100 / f.Size) : 0;
    public bool ShowProgress => _m.File?.State == FileState.Transferring;

    public string FileStateText
    {
        get
        {
            if (_m.File is not { } f) return "";
            if (IsOutgoing && _m.Status is not MessageStatus.Delivered && f.State == FileState.Offered) return StatusText;
            return (IsOutgoing, f.State) switch
            {
                (true, FileState.Offered) => "Esperando que lo acepte",
                (true, FileState.Transferring) => $"Enviando… {Progress} %",
                (true, FileState.Completed) => "Recibido por el compañero",
                (true, FileState.Rejected) => "Rechazado por el compañero",
                (false, FileState.Offered) => "Quiere enviarte este archivo",
                (false, FileState.Transferring) => $"Descargando… {Progress} %",
                (false, FileState.Completed) => "Recibido",
                (false, FileState.Rejected) => "Rechazado",
                (_, FileState.Cancelled) => "Cancelado",
                _ => "Fallido",
            };
        }
    }

    public string? FileError => _m.File?.Error;
    public bool HasFileError => !string.IsNullOrEmpty(_m.File?.Error);
    public bool CanAcceptFile => !IsOutgoing && _m.File?.State == FileState.Offered;
    public bool CanRejectFile => !IsOutgoing && _m.File?.State is FileState.Offered or FileState.Transferring;
    public bool CanCancelFile => IsOutgoing && _m.File?.State is FileState.Offered or FileState.Transferring;
    public bool CanOpenFile => _m.File is { } f && (IsOutgoing ? f.State is not FileState.Failed : f.State == FileState.Completed);

    public void UpdateFile(FileTransfer file)
    {
        _m = _m with { File = file };
        RaiseFile();
    }

    private void RaiseFile()
    {
        foreach (var p in new[] { nameof(File), nameof(FileName), nameof(FileSizeText), nameof(IsDangerousFile), nameof(Progress), nameof(ShowProgress),
                     nameof(FileStateText), nameof(FileError), nameof(HasFileError), nameof(CanAcceptFile), nameof(CanRejectFile), nameof(CanCancelFile), nameof(CanOpenFile) })
            OnPropertyChanged(p);
    }
}

public sealed class StickerItem : ObservableObject
{
    private ImageSource? _thumb;
    private bool _loading;

    private readonly Func<string, Task<byte[]?>> _loader;

    public StickerItem(LibrarySticker s, Func<string, Task<byte[]?>> loader)
    {
        Sticker = s;
        _loader = loader;
    }

    public LibrarySticker Sticker { get; }
    public string Name => Sticker.Name;
    public bool Favorite => Sticker.Favorite;
    public string FavoriteGlyph => Sticker.Favorite ? "" : "";

    public ImageSource? Thumbnail
    {
        get
        {
            if (_thumb is null && !_loading)
            {
                _loading = true;
                _ = Task.Run(async () =>
                {
                    var img = await StickerImageCache.LoadAsync(Sticker.Hash, _loader, 96);
                    Ui.Post(() => { _thumb = img; OnPropertyChanged(nameof(Thumbnail)); });
                });
            }
            return _thumb;
        }
    }
}

public sealed class ChatViewModel : ObservableObject
{
    private const int PageSize = 50;
    private readonly ChatEngine _engine;
    private readonly MainViewModel _main;
    private readonly Dictionary<long, MessageItem> _byId = new();
    private readonly Dictionary<long, string> _drafts = new();
    private Contact? _contact;
    private string _draft = "";
    private string? _inputError;
    private bool _hasOlder;
    private bool _loadingOlder;
    private bool _online;
    private string? _actionMessage;
    private bool _pickerOpen;
    private string _pickerTab = "Todos";
    private string? _pickerMessage;
    private int _undelivered;

    public ChatViewModel(ChatEngine engine, MainViewModel main)
    {
        _engine = engine;
        _main = main;
        SendCommand = new AsyncCommand(_ => SendAsync(), _ => _contact is not null);
        RetryCommand = new AsyncCommand(RetryAsync);
        RequestCommand = new AsyncCommand(async () => await ActionAsync(_engine.SendChatRequestAsync(_contact!.Fingerprint)));
        BlockCommand = new AsyncCommand(async () => await ActionAsync(_engine.BlockAsync(_contact!.Id)));
        UnblockCommand = new AsyncCommand(async () => await ActionAsync(_engine.UnblockAsync(_contact!.Id)));
        ExportCommand = new AsyncCommand(ExportAsync);
        ClearCommand = new AsyncCommand(ClearAsync);
        BackCommand = new RelayCommand(() => _main.ShowPeople());
        TogglePickerCommand = new AsyncCommand(async () => { IsPickerOpen = !IsPickerOpen; if (IsPickerOpen) await LoadStickersAsync(); });
        PickerTabCommand = new AsyncCommand(async p => { PickerTab = p as string ?? "Todos"; await LoadStickersAsync(); });
        SendStickerCommand = new AsyncCommand(async p => { if (p is StickerItem s) await SendStickerAsync(s); });
        FavoriteCommand = new AsyncCommand(async p => { if (p is StickerItem s) { await _engine.Stickers.SetFavoriteAsync(s.Sticker.Id, !s.Favorite); await LoadStickersAsync(); } });
        RemoveStickerCommand = new AsyncCommand(async p =>
        {
            if (p is StickerItem s)
            {
                await _engine.Stickers.RemoveFromLibraryAsync(s.Sticker.Id);
                PickerMessage = "Quitado de la biblioteca. Los mensajes antiguos lo siguen mostrando.";
                await LoadStickersAsync();
            }
        });
        ImportCommand = new AsyncCommand(ImportAsync);
        TestConnectionCommand = new AsyncCommand(TestConnectionAsync);
        AttachCommand = new AsyncCommand(_ => AttachAsync(), _ => CanWrite);
        AcceptFileCommand = new AsyncCommand(async p => { if (p is MessageItem m) ActionMessage = (await _engine.AcceptFileAsync(m.Uid)).Message; });
        RejectFileCommand = new AsyncCommand(async p => { if (p is MessageItem m) ActionMessage = (await _engine.RejectFileAsync(m.Uid)).Message; });
        CancelFileCommand = new AsyncCommand(async p => { if (p is MessageItem m) ActionMessage = (await _engine.CancelFileAsync(m.Uid)).Message; });
        OpenFileCommand = new AsyncCommand(async p => { if (p is MessageItem m) await OpenFileAsync(m); });
        SaveFileCommand = new AsyncCommand(async p => { if (p is MessageItem m) await SaveFileAsync(m); });
    }

    public ObservableCollection<MessageItem> Messages { get; } = new();
    public ObservableCollection<StickerItem> Stickers { get; } = new();

    public event EventHandler<MessageItem>? MessageAppended;
    public event EventHandler? ConversationLoaded;

    public long? ContactId => _contact?.Id;
    public bool HasContact => _contact is not null;
    public string Title => _contact?.DisplayName ?? "Sin conversación";
    public string Machine => _contact?.MachineName ?? "";
    public bool Online { get => _online; private set { Set(ref _online, value); OnPropertyChanged(nameof(AvailabilityText)); } }
    public string AvailabilityText => _contact is null ? "" : Online ? "Disponible" : "Desconectado";
    public string PairingCode => _contact is null ? "" : _engine.PairingCodeWith(_contact.Fingerprint);
    public bool IsBlocked => _contact?.Blocked == true;
    public bool CanWrite => _contact is { Blocked: false, Relation: Relation.Authorized };
    public bool CanRequest => _contact is { Blocked: false } c && c.Relation is Relation.None or Relation.RejectedByPeer or Relation.RevokedByPeer or Relation.RejectedByMe;

    public string? RelationBanner => _contact switch
    {
        null => null,
        { Blocked: true } => "Has bloqueado a esta persona. No recibirás sus mensajes ni solicitudes. El historial se conserva.",
        { Relation: Relation.Authorized } => null,
        { Relation: Relation.OutgoingPending } => "Solicitud enviada. Podrán escribirse cuando la acepte.",
        { Relation: Relation.IncomingPending } => "Esta persona quiere chatear contigo. Acepta o rechaza en Personas.",
        { Relation: Relation.RevokedByPeer } => "Esta persona ya no acepta tus mensajes. Puedes enviar una nueva solicitud.",
        { Relation: Relation.RejectedByPeer } => "Rechazó tu solicitud.",
        _ => "No hay autorización para escribir. Envía una solicitud de chat.",
    };
    public bool HasRelationBanner => RelationBanner is not null;

    public int UndeliveredCount { get => _undelivered; private set { Set(ref _undelivered, value); OnPropertyChanged(nameof(UndeliveredText)); OnPropertyChanged(nameof(HasUndelivered)); } }
    public bool HasUndelivered => _undelivered > 0;
    public string UndeliveredText => _undelivered == 1 ? "1 mensaje sin entregar" : $"{_undelivered} mensajes sin entregar";

    public string Draft
    {
        get => _draft;
        set
        {
            if (!Set(ref _draft, value)) return;
            if (_contact is not null) _drafts[_contact.Id] = value;
            if (InputError is not null && value.Length > 0) InputError = null;
        }
    }

    public string? InputError { get => _inputError; set { Set(ref _inputError, value); OnPropertyChanged(nameof(HasInputError)); } }
    public bool HasInputError => !string.IsNullOrEmpty(_inputError);
    public string? ActionMessage { get => _actionMessage; set { Set(ref _actionMessage, value); OnPropertyChanged(nameof(HasActionMessage)); } }
    public bool HasActionMessage => !string.IsNullOrEmpty(_actionMessage);
    public bool HasOlder { get => _hasOlder; private set => Set(ref _hasOlder, value); }

    public bool IsPickerOpen { get => _pickerOpen; set => Set(ref _pickerOpen, value); }
    public string PickerTab { get => _pickerTab; set => Set(ref _pickerTab, value); }
    public string? PickerMessage { get => _pickerMessage; set { Set(ref _pickerMessage, value); OnPropertyChanged(nameof(HasPickerMessage)); } }
    public bool HasPickerMessage => !string.IsNullOrEmpty(_pickerMessage);
    public bool StickersEmpty => Stickers.Count == 0;

    public AsyncCommand SendCommand { get; }
    public AsyncCommand RetryCommand { get; }
    public AsyncCommand RequestCommand { get; }
    public AsyncCommand BlockCommand { get; }
    public AsyncCommand UnblockCommand { get; }
    public AsyncCommand ExportCommand { get; }
    public AsyncCommand ClearCommand { get; }
    public RelayCommand BackCommand { get; }
    public AsyncCommand TogglePickerCommand { get; }
    public AsyncCommand PickerTabCommand { get; }
    public AsyncCommand SendStickerCommand { get; }
    public AsyncCommand FavoriteCommand { get; }
    public AsyncCommand RemoveStickerCommand { get; }
    public AsyncCommand ImportCommand { get; }
    public AsyncCommand TestConnectionCommand { get; }
    public AsyncCommand AttachCommand { get; }
    public AsyncCommand AcceptFileCommand { get; }
    public AsyncCommand RejectFileCommand { get; }
    public AsyncCommand CancelFileCommand { get; }
    public AsyncCommand OpenFileCommand { get; }
    public AsyncCommand SaveFileCommand { get; }

    /// <summary>Progreso o cambio de estado de un documento.</summary>
    public void OnFileChanged(FileEvent e)
    {
        if (_contact is null || e.ContactId != _contact.Id) return;
        if (_byId.TryGetValue(e.MessageId, out var item)) item.UpdateFile(e.File);
    }

    private async Task AttachAsync()
    {
        if (_contact is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Enviar documentos (máximo {MiniChat.Core.Files.FileNames.FormatSize(_engine.Options.MaxFileBytes)} cada uno)",
            Multiselect = true,
            Filter = "Todos los archivos|*.*",
        };
        if (dialog.ShowDialog() != true) return;
        foreach (var path in dialog.FileNames)
        {
            ActionMessage = $"Preparando «{Path.GetFileName(path)}»…";
            var r = await _engine.SendFileAsync(_contact.Id, path);
            ActionMessage = r.Success ? null : $"«{Path.GetFileName(path)}»: {r.Message}";
            if (!r.Success) break;
        }
    }

    private async Task OpenFileAsync(MessageItem m)
    {
        if (m.IsDangerousFile && !ConfirmDangerous(m.FileName)) return;
        ActionMessage = "Abriendo…";
        var r = await _engine.PrepareOpenFileAsync(m.Uid);
        if (!r.Success) { ActionMessage = r.Message; return; }
        ActionMessage = "El documento se abrió desde una copia temporal sin cifrar, que se borrará al bloquear MiniChat.";
        try
        {
            // Nunca se ejecuta sin que el usuario lo pida; Windows además lo trata como descargado de otro equipo.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(r.Value!.Path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ActionMessage = "Windows no pudo abrirlo (¿no hay un programa para este tipo de archivo?): " + ex.Message;
        }
    }

    private async Task SaveFileAsync(MessageItem m)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Guardar documento (la copia guardada NO estará cifrada)",
            FileName = m.FileName,
            Filter = "Todos los archivos|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog() != true) return;
        ActionMessage = "Guardando…";
        var r = await _engine.SaveFileAsync(m.Uid, dialog.FileName);
        ActionMessage = r.Message;
    }

    private static bool ConfirmDangerous(string name) =>
        MessageBox.Show(Application.Current.MainWindow!,
            $"«{name}» es un tipo de archivo que puede ejecutar programas o cambiar la configuración del equipo " +
            "(ejecutables, scripts, instaladores, accesos directos o documentos con macros).\n\n" +
            "Ábralo solo si esperaba este archivo y confía en quien lo envió. Windows puede mostrar además su propia advertencia.\n\n¿Abrirlo?",
            "Archivo potencialmente peligroso", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    // ======================= Abrir y paginar =======================

    public async Task OpenAsync(long contactId)
    {
        if (_contact?.Id == contactId) { await MarkReadAsync(); return; }
        await SaveDraftAsync();
        _contact = await _engine.GetContactAsync(contactId);
        Messages.Clear();
        _byId.Clear();
        ActionMessage = null;
        InputError = null;
        IsPickerOpen = false;
        if (_contact is null) { RaiseAll(); return; }

        var page = await _engine.GetMessagesAsync(contactId, null, PageSize);
        foreach (var m in page) Add(m, atEnd: true);
        HasOlder = page.Count == PageSize;
        _draft = _drafts.TryGetValue(contactId, out var d) ? d : await _engine.GetDraftAsync(contactId) ?? "";
        _drafts[contactId] = _draft;
        OnPropertyChanged(nameof(Draft));
        Online = _engine.IsOnline(_contact.Fingerprint);
        RecountUndelivered();
        RaiseAll();
        ConversationLoaded?.Invoke(this, EventArgs.Empty);
        await MarkReadAsync();
    }

    /// <summary>Carga el bloque anterior; devuelve cuántos mensajes se insertaron al principio.</summary>
    public async Task<int> LoadOlderAsync()
    {
        if (_contact is null || !HasOlder || _loadingOlder || Messages.Count == 0) return 0;
        _loadingOlder = true;
        try
        {
            var page = await _engine.GetMessagesAsync(_contact.Id, Messages[0].Id, PageSize);
            for (int i = page.Count - 1; i >= 0; i--) Add(page[i], atEnd: false);
            HasOlder = page.Count == PageSize;
            return page.Count;
        }
        finally
        {
            _loadingOlder = false;
        }
    }

    private MessageItem Add(ChatMessage m, bool atEnd)
    {
        var item = new MessageItem(m, LoadStickerBytesAsync);
        _byId[m.Id] = item;
        if (atEnd) Messages.Add(item); else Messages.Insert(0, item);
        return item;
    }

    // ======================= Eventos del motor =======================

    public void OnMessageChanged(MessageEvent e)
    {
        if (_contact is null || e.ContactId != _contact.Id) return;
        if (_byId.TryGetValue(e.Message.Id, out var existing))
        {
            existing.Update(e.Message);
        }
        else if (Messages.Count == 0 || e.Message.Id > Messages[^1].Id)
        {
            var item = Add(e.Message, atEnd: true);
            MessageAppended?.Invoke(this, item);
        }
        RecountUndelivered();
    }

    public async Task OnContactChangedAsync(long contactId)
    {
        if (_contact?.Id != contactId) return;
        _contact = await _engine.GetContactAsync(contactId);
        if (_contact is not null) Online = _engine.IsOnline(_contact.Fingerprint);
        RaiseAll();
    }

    public void RefreshAvailability()
    {
        if (_contact is not null) Online = _engine.IsOnline(_contact.Fingerprint);
    }

    public async Task MarkReadAsync()
    {
        if (_contact is not null && _main.IsChatVisible) await _engine.MarkReadAsync(_contact.Id);
    }

    private void RecountUndelivered() =>
        UndeliveredCount = Messages.Count(m => m.IsOutgoing && m.Status is MessageStatus.Failed or MessageStatus.Unconfirmed or MessageStatus.Pending);

    private void RaiseAll()
    {
        foreach (var p in new[] { nameof(ContactId), nameof(HasContact), nameof(Title), nameof(Machine), nameof(AvailabilityText), nameof(PairingCode),
                     nameof(IsBlocked), nameof(CanWrite), nameof(CanRequest), nameof(RelationBanner), nameof(HasRelationBanner) })
            OnPropertyChanged(p);
    }

    // ======================= Acciones =======================

    private async Task SendAsync()
    {
        if (_contact is null) return;
        var text = Draft;
        if (string.IsNullOrWhiteSpace(text)) return;
        var result = await _engine.SendTextAsync(_contact.Id, text);
        if (result.Success)
        {
            // Solo se vacía la caja si el mensaje quedó guardado; si no, el texto se conserva.
            Draft = "";
            InputError = null;
            _ = _engine.SaveDraftAsync(_contact.Id, null);
        }
        else
        {
            InputError = result.Message;
        }
    }

    private async Task SendStickerAsync(StickerItem s)
    {
        if (_contact is null) return;
        var result = await _engine.SendStickerAsync(_contact.Id, s.Sticker.Hash);
        if (result.Success) IsPickerOpen = false;
        else PickerMessage = result.Message;
    }

    private async Task RetryAsync()
    {
        if (_contact is null) return;
        var r = await _engine.RetryAsync(_contact.Id);
        ActionMessage = r.Message;
    }

    private async Task ActionAsync(Task<OperationResult> action)
    {
        var r = await action;
        ActionMessage = r.Message;
        if (_contact is not null) await OnContactChangedAsync(_contact.Id);
        _main.People.QueueRefresh();
    }

    private async Task ExportAsync()
    {
        if (_contact is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exportar conversación a texto (el archivo NO estará cifrado)",
            FileName = $"MiniChat - {Sanitize(_contact.DisplayName)} - {DateTime.Now:yyyy-MM-dd}.txt",
            Filter = "Texto (*.txt)|*.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var r = await _engine.ExportAsync(_contact.Id, dialog.FileName);
            ActionMessage = $"Exportados {r.MessageCount} mensajes." + (r.StickerFolder is null ? "" : $" Los {r.StickerCount} stickers están en «{Path.GetFileName(r.StickerFolder)}».")
                + " Atención: el archivo exportado NO está cifrado; guárdelo en un lugar seguro o bórrelo cuando ya no lo necesite.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActionMessage = "No se pudo exportar: " + ex.Message;
        }
    }

    private async Task ClearAsync()
    {
        if (_contact is null) return;
        var answer = MessageBox.Show(Application.Current.MainWindow!,
            $"¿Borrar el historial de la conversación con {_contact.DisplayName} en ESTE equipo?\n\n" +
            "No se puede deshacer. La copia que tiene tu compañero en su equipo NO se borra. El contacto y la autorización se mantienen.",
            "Borrar historial local", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        var n = await _engine.ClearHistoryAsync(_contact.Id);
        Messages.Clear();
        _byId.Clear();
        HasOlder = false;
        RecountUndelivered();
        ActionMessage = $"Se borraron {n} mensajes de este equipo.";
    }

    private async Task ImportAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Importar stickers",
            Multiselect = true,
            Filter = "Imágenes (PNG, JPG, WebP)|*.png;*.jpg;*.jpeg;*.webp|Todos los archivos|*.*",
        };
        if (dialog.ShowDialog() != true) return;
        PickerMessage = "Importando…";
        var results = await _engine.Stickers.ImportAsync(dialog.FileNames);
        int ok = results.Count(r => r.Success && !r.AlreadyInLibrary);
        int dup = results.Count(r => r.AlreadyInLibrary);
        var failed = results.Where(r => !r.Success).ToList();
        var notes = results.Where(r => r.Success && r.Message.Contains("fotograma")).ToList();
        var msg = $"{ok} importado(s)";
        if (dup > 0) msg += $", {dup} ya estaba(n)";
        if (notes.Count > 0) msg += $". {notes.Count} animado(s): se conservó solo el primer fotograma";
        if (failed.Count > 0) msg += $". {failed.Count} rechazado(s): " + string.Join("; ", failed.Take(3).Select(f => $"{Path.GetFileName(f.SourcePath)}: {f.Message}"));
        PickerMessage = msg + ".";
        PickerTab = "Todos";
        await LoadStickersAsync();
    }

    public async Task LoadStickersAsync()
    {
        var list = PickerTab switch
        {
            "Recientes" => await _engine.Stickers.GetRecentAsync(24),
            "Favoritos" => (await _engine.Stickers.GetLibraryAsync()).Where(s => s.Favorite).ToList(),
            _ => await _engine.Stickers.GetLibraryAsync(),
        };
        Stickers.Clear();
        foreach (var s in list) Stickers.Add(new StickerItem(s, LoadStickerBytesAsync));
        OnPropertyChanged(nameof(StickersEmpty));
    }

    private async Task<byte[]?> LoadStickerBytesAsync(string hash)
    {
        try { return await _engine.Stickers.ReadBlobAsync(hash, CancellationToken.None); }
        catch (MiniChat.Core.Security.VaultLockedException) { return null; }
    }

    /// <summary>Al bloquear: guarda el borrador (cifrado) y quita de memoria todo lo visible.</summary>
    public async Task LockAsync()
    {
        await SaveDraftAsync();
        _resumeContactId = _contact?.Id;
        _contact = null;
        Messages.Clear();
        _byId.Clear();
        _drafts.Clear();
        Stickers.Clear();
        _draft = "";
        OnPropertyChanged(nameof(Draft));
        IsPickerOpen = false;
        ActionMessage = null;
        InputError = null;
        HasOlder = false;
        RecountUndelivered();
        RaiseAll();
    }

    /// <summary>Al desbloquear: vuelve a abrir la conversación que estaba abierta.</summary>
    public async Task ResumeAsync()
    {
        if (_resumeContactId is long id) await OpenAsync(id);
        _resumeContactId = null;
    }

    private long? _resumeContactId;

    private async Task TestConnectionAsync()
    {
        if (_contact is null) return;
        ActionMessage = "Probando la conexión…";
        var r = await _engine.TestConnectionAsync(_contact.Id);
        ActionMessage = r.Summary;
        RefreshAvailability();
    }

    public async Task SaveDraftAsync()
    {
        if (_contact is not null) await _engine.SaveDraftAsync(_contact.Id, _drafts.GetValueOrDefault(_contact.Id));
    }

    private static string Sanitize(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
