namespace MiniChat.Core.Config;

/// <summary>Ubicación de los datos modificables de un perfil (nunca dentro de Program Files).</summary>
public sealed class AppPaths
{
    public AppPaths(string dataDirectory)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
    }

    /// <summary>%LocalAppData%\MiniChatLAN\ para el perfil por defecto; ...\profiles\&lt;nombre&gt;\ para otros.</summary>
    public static AppPaths ForProfile(string? profile)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniChatLAN");
        if (string.IsNullOrWhiteSpace(profile) || profile == "default") return new AppPaths(root);
        foreach (var c in Path.GetInvalidFileNameChars())
            if (profile.Contains(c)) throw new ArgumentException("Nombre de perfil no válido.", nameof(profile));
        return new AppPaths(Path.Combine(root, "profiles", profile));
    }

    public string DataDirectory { get; }
    public string DatabaseFile => Path.Combine(DataDirectory, "minichat.db");
    public string StickerDirectory => Path.Combine(DataDirectory, "stickers");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");
    public string BackupDirectory => Path.Combine(DataDirectory, "backups");
    /// <summary>Documentos adjuntos, cifrados (*.mcf) y descargas en curso (*.part).</summary>
    public string FilesDirectory => Path.Combine(DataDirectory, "files");
    /// <summary>Copias descifradas temporales de documentos abiertos (se limpian al bloquear y al arrancar).</summary>
    public string OpenedDirectory => Path.Combine(DataDirectory, "abiertos");

    /// <summary>
    /// Deja la carpeta de datos accesible solo para el usuario actual y SYSTEM (sin herencia de permisos).
    /// Otros usuarios del equipo dejan de poder abrirla. Un administrador podría tomar posesión de la carpeta,
    /// pero es una acción explícita y auditable; y aun así el contenido está cifrado.
    /// Devuelve null si se aplicó o el motivo si no se pudo.
    /// </summary>
    public string? RestrictToCurrentUser()
    {
        try
        {
            var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
            var system = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
            var security = new System.Security.AccessControl.DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { user, system })
            {
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));
            }
            new DirectoryInfo(DataDirectory).SetAccessControl(security);
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or System.Security.SecurityException)
        {
            return ex.Message;
        }
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(StickerDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
