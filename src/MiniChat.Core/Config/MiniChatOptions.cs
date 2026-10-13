using System.Net;

namespace MiniChat.Core.Config;

/// <summary>
/// Todos los parámetros de red, protocolo y límites en un único lugar.
/// Los valores por defecto son los de producción; las pruebas crean copias con "with".
/// </summary>
public sealed record MiniChatOptions
{
    // ---------- Descubrimiento (UDP multicast, alcance administrativo local) ----------
    /// <summary>Grupo multicast de ámbito local administrado (RFC 2365). TTL 1: nunca sale de la subred.</summary>
    public IPAddress DiscoveryGroup { get; init; } = IPAddress.Parse("239.255.77.77");
    public int DiscoveryPort { get; init; } = 47820;
    public TimeSpan AnnounceInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan AnnounceJitter { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Un equipo que no se anuncia durante este tiempo deja de figurar como disponible.</summary>
    public TimeSpan PresenceTimeout { get; init; } = TimeSpan.FromSeconds(35);
    public TimeSpan PresenceSweepInterval { get; init; } = TimeSpan.FromSeconds(5);
    public int MaxDatagramBytes { get; init; } = 1024;
    /// <summary>Datagramas procesados por segundo como máximo (el resto se descarta).</summary>
    public int MaxDatagramsPerSecond { get; init; } = 100;
    public int MaxDatagramsPerSourcePerSecond { get; init; } = 10;
    public int MaxDiscoveredPeers { get; init; } = 256;
    /// <summary>Intervalo mínimo entre respuestas a consultas de descubrimiento.</summary>
    public TimeSpan QueryReplyMinInterval { get; init; } = TimeSpan.FromSeconds(2);

    // ---------- Otras subredes de la oficina (unicast, solo las autorizadas) ----------
    /// <summary>Sondeos por segundo al buscar compañeros en otras subredes (una /24 tarda unos 13 s).</summary>
    public int RemoteProbesPerSecond { get; init; } = 20;
    /// <summary>Pausa entre dos búsquedas completas de las subredes autorizadas.</summary>
    public TimeSpan RemoteSweepInterval { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>Intervalo mínimo entre dos respuestas a sondeos del mismo origen.</summary>
    public TimeSpan ProbeReplyMinInterval { get; init; } = TimeSpan.FromSeconds(5);
    public int MaxRemoteSubnets { get; init; } = 32;

    // ---------- Transporte (TCP + TLS) ----------
    public int TcpPort { get; init; } = 47821;
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Las conexiones sin actividad se cierran tras este tiempo.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public int MaxInboundConnections { get; init; } = 32;
    public int MaxInboundConnectionsPerAddress { get; init; } = 4;
    public int MaxFramesPerSecondPerConnection { get; init; } = 20;
    public int MaxMalformedFramesPerConnection { get; init; } = 3;

    // ---------- Protocolo ----------
    public int MaxFrameBytes { get; init; } = 1536 * 1024;
    public int MaxHeaderBytes { get; init; } = 16 * 1024;
    public int MaxTextChars { get; init; } = 4000;
    public int MaxNameChars { get; init; } = 64;

    // ---------- Solicitudes ----------
    public TimeSpan RequestExpiry { get; init; } = TimeSpan.FromDays(7);
    /// <summary>Tras rechazar a alguien, sus nuevas solicitudes se ignoran durante este tiempo.</summary>
    public TimeSpan RequestCooldownAfterReject { get; init; } = TimeSpan.FromMinutes(10);
    public int MaxRequestsPerPeerPerHour { get; init; } = 3;
    public int MaxIncomingRequestsPerHour { get; init; } = 30;
    /// <summary>Espera mínima entre intentos automáticos de entregar una aceptación/rechazo pendiente.</summary>
    public TimeSpan DecisionRedeliveryMinInterval { get; init; } = TimeSpan.FromSeconds(30);

    // ---------- Stickers ----------
    public long MaxStickerImportFileBytes { get; init; } = 5 * 1024 * 1024;
    public int MaxStickerSourceDimension { get; init; } = 4096;
    public long MaxStickerSourcePixels { get; init; } = 4096L * 4096L;
    /// <summary>Los stickers se normalizan a PNG con este lado máximo.</summary>
    public int StickerMaxDimension { get; init; } = 512;
    public int MaxStickerWireBytes { get; init; } = 1024 * 1024;
    public int MaxConcurrentImageDecodes { get; init; } = 2;

    // ---------- Documentos adjuntos ----------
    /// <summary>Tamaño máximo por documento.</summary>
    public long MaxFileBytes { get; init; } = 100L * 1024 * 1024;
    /// <summary>Tamaño de cada trozo transferido (cabe en una trama de MaxFrameBytes).</summary>
    public int FileChunkBytes { get; init; } = 1024 * 1024;
    /// <summary>Descargas simultáneas como máximo.</summary>
    public int MaxConcurrentFileTransfers { get; init; } = 2;

    // ---------- Cifrado del historial ----------
    /// <summary>Iteraciones PBKDF2-SHA256 para derivar la clave del PIN (recomendación OWASP 2023: ≥ 600 000).</summary>
    public int PinIterations { get; init; } = 600_000;
    /// <summary>Intentos de PIN fallidos permitidos antes de imponer esperas crecientes.</summary>
    public int PinFreeAttempts { get; init; } = 3;

    // ---------- Pruebas ----------
    /// <summary>Solo para pruebas en una PC: permite la interfaz de loopback. Nunca se expone en la interfaz.</summary>
    public bool AllowLoopback { get; init; }
}
