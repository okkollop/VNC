# InfollopAssist — escritorio remoto para Windows

Dos programas, sin instalación ni .NET:

- **InfollopAssistServidor.exe**: en el equipo que quieres controlar. Vive junto al reloj;
  al pasar el ratón por su icono se ve la IP y el puerto.
- **InfollopAssistVisor.exe**: en el equipo desde el que te conectas. Libreta de equipos por carpetas.

Puerto por defecto: **6900**.

## Servidor

1. Abre `InfollopAssistServidor.exe`. La primera vez pide la contraseña (sólo cuentan 8 caracteres,
   como en VNC).
2. Clic en el icono del reloj: ventana de estado con la IP, la contraseña y quién está conectado.
   Menú (☰ o clic derecho): opciones, chat, carpeta de recibidos, desconectar, actualizaciones.
3. **Opciones → General → Ejecutar como servicio** (recomendado): lo instala en
   `C:\Program Files\InfollopAssist Servidor` como servicio de Windows (servicio `InfollopAssistServidor`). Así se ve también la pantalla de inicio
   de sesión, la de bloqueo y las ventanas de administrador, Ctrl+Alt+Supr funciona y arranca aunque
   nadie inicie sesión. Se quita desde el mismo interruptor. Pide permiso de administrador.

La configuración del servidor está en `C:\ProgramData\InfollopAssist` (contraseña cifrada para el equipo).

## Visor

- Escribe la IP arriba y pulsa **Conectar**, o guárdalo con **Archivo → Nueva conexión**.
- Doble clic en un equipo para conectar: la primera vez pide la contraseña y pregunta si guardarla.
- **Archivo → Importar de RealVNC**: lee los `.vnc` exportados de RealVNC Viewer (la etiqueta pasa a ser la carpeta).
- Dentro de una conexión, la barra de iconos de arriba aparece al acercar el ratón: pantalla completa,
  ajustar, Ctrl+Alt+Supr, tecla Windows, chat, enviar archivos, carpeta de recibidos, copiar y pegar
  archivos y desconectar.

## Actualizaciones

Los programas consultan `https://soporte.infollop.net/infollopassist/version.json`.
El servidor se actualiza solo (cada 6 horas, cuando nadie está conectado). El visor pregunta al abrirse.
Cada versión va firmada: si la firma o la huella del archivo no coinciden, no se instala.

### Publicar una versión nueva

1. En GitHub: **Actions → Publicar versión → Run workflow**. Escribe el número (p. ej. `1.0.1`,
   siempre mayor que el anterior) y qué cambia.
2. GitHub compila, firma y publica una *release* `v<versión>` con tres archivos
   (`InfollopAssistServidor.exe`, `InfollopAssistVisor.exe`, `version.json`).
3. El servidor de soporte.infollop.net comprueba cada 10 minutos la última release y copia
   los archivos a `infollopassist/` (primero los dos `.exe`, al final `version.json`).
   Script: `/usr/local/bin/infollopassist-sync.sh`; registro en `/var/log/infollopassist-sync.log`.

Único secreto de GitHub (Settings → Secrets and variables → Actions → New repository secret):
`INFOLLOP_CLAVE`, la clave privada de firma. Guárdala también fuera de GitHub: sin ella no se pueden
publicar actualizaciones. Nunca la subas al repositorio, que es público.

## Limitaciones

- La conexión no va cifrada (la contraseña sí viaja protegida). Úsalo en tu red o por VPN; no abras
  el puerto a Internet.
- Para conectar a equipos con RealVNC Server hay que configurar en ellos «cifrado: preferir desactivado»
  y «autenticación: contraseña VNC», o instalar InfollopAssist Servidor.
- Chat, archivos y portapapeles de archivos sólo funcionan entre programas InfollopAssist.
- Ambos equipos deberían usar la misma distribución de teclado.

## Compilar

Requiere el SDK de .NET 8.

```
dotnet test tests/Enlace.Protocol.Tests
dotnet publish src/Enlace.Server -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o dist
dotnet publish src/Enlace.Client -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o dist
```

© 2026 Infollop. Todos los derechos reservados.
