# Enlace — escritorio remoto para Windows

Dos programas:

- **EnlaceServidor.exe**: en el equipo que quieres controlar. Se queda junto al reloj.
- **EnlaceVisor.exe**: en el equipo desde el que te conectas.

No necesitan instalación ni tener .NET instalado.

## Uso

1. En el equipo remoto abre `EnlaceServidor.exe`. La primera vez pide una contraseña
   (sólo cuentan 8 caracteres, como en VNC). Windows preguntará por el firewall: permite
   el acceso en redes privadas.
2. Haz clic derecho en el icono de la bandeja (junto al reloj) para ver la IP del equipo.
3. En el otro equipo abre `EnlaceVisor.exe`. Escribe la IP arriba y pulsa **Conectar**, o guárdalo
   con **Nuevo equipo** (nombre, dirección y carpeta). Doble clic en un equipo para conectar: la primera
   vez pide la contraseña y pregunta si quieres guardarla.

## Traer los equipos de RealVNC

En RealVNC Viewer exporta las conexiones (crea un archivo `.vnc` por equipo). En Enlace Visor:
**Importar de RealVNC → Carpeta con archivos .vnc…** y elige la carpeta. Se usa el nombre, la
dirección, la etiqueta como carpeta y la fecha de última conexión. Las contraseñas no vienen en
esos archivos: se piden al conectar. Si importas otra vez, se actualizan en lugar de duplicarse.

Importante: un equipo con RealVNC Server sólo acepta, por defecto, el cifrado propio de RealVNC,
que Enlace no puede usar. Para conectar a él hay que instalar Enlace Servidor en ese equipo o, en
RealVNC Server, poner el cifrado en «preferir desactivado» y la autenticación en «contraseña VNC».

Si en el equipo remoto también está RealVNC Server, los dos querrán el puerto 5900:
cierra uno o cambia el puerto en *Configuración* (en el visor escribe `IP:puerto`).

## Qué hace

- Ver y controlar la pantalla (todas las pantallas del equipo remoto).
- Portapapeles de texto en los dos sentidos.
- Copiar archivos o carpetas en un equipo y pegarlos en el otro con Ctrl+V
  (botón *Archivos por portapapeles*; límite configurable, 2 GB por defecto).
- *Enviar archivos…* o arrastrar archivos a la ventana: llegan a `Descargas\Enlace` del equipo remoto.
- Tecla Windows, Alt+Tab y Ctrl+Esc se envían al equipo remoto mientras la ventana tiene el foco.

Es compatible con visores VNC estándar (RealVNC Viewer, TigerVNC…) para ver, controlar y
copiar texto, si el visor usa autenticación VNC normal sin cifrado propio.

## Limitaciones

- El servidor es una aplicación normal del usuario: no se ve la pantalla de inicio de sesión,
  la de bloqueo ni las ventanas de UAC, y Ctrl+Alt+Supr no tiene efecto.
- La conexión no va cifrada (la contraseña sí viaja protegida). Úsalo en tu red local o a través
  de una VPN; no abras el puerto 5900 a Internet.
- Las dos máquinas deberían usar la misma distribución de teclado (por ejemplo, español).

## Compilar

Requiere el SDK de .NET 8.

```
dotnet test tests/Enlace.Protocol.Tests
dotnet publish src/Enlace.Server -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o dist
dotnet publish src/Enlace.Client -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o dist
```
