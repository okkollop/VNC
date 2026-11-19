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
3. En el otro equipo abre `EnlaceVisor.exe`, escribe la IP y la contraseña y pulsa **Conectar**.

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
