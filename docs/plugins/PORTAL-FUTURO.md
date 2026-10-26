# Portal de desarrolladores (arquitectura futura, sin implementar)

Hoy publica un solo administrador (Angelito Systems S.A.C.), con una sola clave. Este documento fija cómo abrir el
marketplace a terceros **sin romper lo que ya existe**: la app, el formato `.lsplugin`, la firma por paquete y el
catálogo no cambian. Solo cambia quién firma y cómo se decide qué entra.

## Principios

1. **La app solo confía en firmas que puede verificar sin conexión.** Hoy usa una clave de publicador. Mañana usará
   una **clave raíz de LiveStream** que certifica las claves de cada desarrollador.
2. **Revisión antes de publicar.** Ningún paquete de un tercero llega al catálogo sin pasar por la cola de revisión.
3. **Revocable.** Se puede retirar una versión, un plugin o un desarrollador entero sin publicar una nueva versión de
   la app.

## Componentes

| Componente | Responsabilidad |
|---|---|
| **Cuentas** | Registro con correo verificado y 2FA obligatorio. OAuth de GitHub como opción para enlazar el repositorio. |
| **Verificación de identidad** | Niveles: *correo verificado*, *organización verificada* (dominio con DNS TXT o documento legal) y *socio*. |
| **Claves de desarrollador** | Cada desarrollador sube su clave pública ECDSA P-256 y la raíz emite un certificado `{developerId, publicKey, validUntil, nivel}` firmado. La privada nunca sale del desarrollador. |
| **Subida** | El mismo `lsplugin publish`, con el token personal del desarrollador. El servidor verifica la firma, el manifiesto y que el `id` pertenezca a su espacio (`com.<desarrollador>.*`). |
| **Cola de revisión** | Análisis automático (antivirus en el contenedor, tamaño, permisos pedidos frente a los usados, ejecutables firmados con Authenticode). Después, revisión manual con lista de comprobación. Estados: *pendiente*, *aprobado*, *rechazado* con motivo. |
| **Distintivos** | *Oficial* (Angelito Systems), *Verificado* (organización verificada y revisión superada) y *Comunidad* (solo correo verificado). Se muestran en la ficha y en la tarjeta del catálogo. |
| **Revocación** | Lista de revocación firmada por la raíz (`/v1/revocations`), que la app consulta junto al catálogo. Un desarrollador o una versión revocados se desactivan con un aviso. |
| **Métricas** | Descargas agregadas, sin datos personales de los usuarios de la app. |

## Cambios necesarios cuando se implemente

- **Catálogo**: cada entrada añade `developer` (id, nombre, distintivo) y `certificate` (el certificado de la clave
  que firmó). Los campos nuevos se ignoran en las versiones antiguas de la app, que siguen aceptando las entradas
  firmadas con la clave actual.
- **App**: `PluginTrust` pasa a contener la clave raíz además de la del publicador. Una entrada es válida si la firma
  la hizo una clave certificada por la raíz, no caducada y no revocada.
- **Backend**: pasa de JSON en un volumen a PostgreSQL (cuentas, revisiones, auditoría) más almacenamiento de objetos
  S3 para los paquetes. La API pública `/v1/catalog` y `/v1/packages` se mantiene.
- **Portal web**: panel del desarrollador (paquetes, estado de revisión, claves, métricas) y panel de revisión
  interno con roles *revisor* y *administrador*, con registro de auditoría de cada decisión.

## Riesgos que hay que resolver antes

- **Aislamiento real**: hoy un plugin es un proceso con los permisos del usuario. Para aceptar código de terceros
  conviene ejecutarlo en un **AppContainer** o con un token restringido, con acceso a archivos y red solo si se
  declara en el manifiesto.
- **Licencias**: LiveStream usa libobs (GPLv2+). Los plugins no enlazan con libobs y se comunican por JSON-RPC, pero
  hay que confirmar con asesoría legal las condiciones de distribución para terceros.
- **Condiciones de uso del marketplace**: responsabilidad, retirada de contenido y datos personales.
