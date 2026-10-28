using LiveStream.Application.Studio;

namespace LiveStream.Application.Extensions;

/// <summary>Descripción estable de una extensión: identidad, versión, de qué depende y qué permisos usa.</summary>
public sealed record ExtensionManifest(
    string Id,
    string Name,
    Version Version,
    string Description,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> Permissions,
    bool CanDisable = true);

/// <summary>Permisos que una extensión debe declarar para usar los servicios sensibles del contexto.</summary>
public static class ExtensionPermissions
{
    /// <summary>Leer y modificar el proyecto, las escenas y los gráficos.</summary>
    public const string Studio = "studio";

    /// <summary>Leer credenciales del almacén cifrado (claves de API, tokens de plataformas).</summary>
    public const string Secrets = "secrets";

    /// <summary>Conexiones de red salientes.</summary>
    public const string Network = "network";

    /// <summary>Reproducir audio (sonidos, voz).</summary>
    public const string Audio = "audio";
}

/// <summary>
/// Una funcionalidad del estudio que vive fuera del núcleo. Ciclo de vida:
/// <see cref="Register"/> (crea y publica sus servicios, sin efectos externos) →
/// <see cref="StartAsync"/> (se suscribe a eventos, arranca temporizadores y conexiones) ⇄
/// <see cref="StopAsync"/> (deshace todo lo de StartAsync) → <see cref="IAsyncDisposable.DisposeAsync"/>.
/// Una extensión desactivada queda registrada (la interfaz puede mostrar su estado) pero inactiva.
/// </summary>
public interface IStudioExtension : IAsyncDisposable
{
    ExtensionManifest Manifest { get; }

    void Register(IExtensionContext context, IExtensionServices services);

    Task StartAsync(CancellationToken ct);

    Task StopAsync(CancellationToken ct);
}

/// <summary>Lo que el núcleo ofrece a una extensión. Los accesos sensibles exigen el permiso declarado.</summary>
public interface IExtensionContext
{
    ExtensionManifest Manifest { get; }

    /// <summary>Proyecto, motor, comandos y gráficos (permiso <see cref="ExtensionPermissions.Studio"/>).</summary>
    StudioSession Session { get; }

    /// <summary>Credenciales cifradas (permiso <see cref="ExtensionPermissions.Secrets"/>).</summary>
    Abstractions.ISecretStore Secrets { get; }

    StudioEventBus Events { get; }

    /// <summary>Ajustes propios de la extensión, persistidos por el host.</summary>
    IReadOnlyDictionary<string, string> Settings { get; }

    void SaveSettings(IReadOnlyDictionary<string, string> values);

    void Log(string level, string message, Exception? ex = null);
}

/// <summary>Registro de servicios entre extensiones: se publican en Register y se consumen por tipo.</summary>
public interface IExtensionServices
{
    void Publish<T>(T service) where T : class;

    /// <summary>Servicio de una dependencia declarada; falla si no está publicado.</summary>
    T Require<T>() where T : class;
}

/// <summary>Persistencia del estado de activación y de los ajustes de cada extensión (por equipo).</summary>
public interface IExtensionStateStore
{
    bool IsEnabled(string id);

    void SetEnabled(string id, bool enabled);

    IReadOnlyDictionary<string, string> LoadSettings(string id);

    void SaveSettings(string id, IReadOnlyDictionary<string, string> values);
}

public sealed class ExtensionPermissionException(string extensionId, string permission)
    : InvalidOperationException($"La extensión «{extensionId}» no declaró el permiso «{permission}».");

public enum ExtensionState
{
    /// <summary>Servicios publicados, sin arrancar.</summary>
    Registered,
    Running,

    /// <summary>Desactivada por el usuario.</summary>
    Disabled,

    /// <summary>Falló al registrarse, arrancar o repetidamente en sus manejadores; el resto de la app sigue.</summary>
    Faulted,

    /// <summary>Una dependencia falta, está desactivada o ha fallado.</summary>
    Blocked,
}

public sealed record ExtensionStatus(ExtensionManifest Manifest, ExtensionState State, bool Enabled, string? Error);
