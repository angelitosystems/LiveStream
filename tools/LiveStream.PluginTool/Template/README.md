# __NAME__

Plugin de ejemplo para LiveStream.

## Compilar y probar

```powershell
dotnet publish -c Release            # sale en bin\Release\net10.0\win-x64\
lsplugin validate bin\Release\net10.0\win-x64\
```

En LiveStream: **Herramientas → Marketplace de plugins → Modo desarrollador** (una sola vez) y luego
«Instalar desde archivo…» con el `.lsplugin` de `lsplugin pack`.

## Publicar (firma verificada)

```powershell
lsplugin pack bin\Release\net10.0\win-x64\ -o <id>-<versión>.lsplugin
lsplugin sign <paquete>.lsplugin --key %USERPROFILE%\.livestream-plugins\private.pem
lsplugin publish <paquete>.lsplugin --server https://<marketplace>
```

Documentación completa: https://github.com/angelitosystems/LiveStream/blob/main/docs/plugins/SDK.md
