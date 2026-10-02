# MineMount - Launcher de Minecraft

Launcher moderno para Minecraft desarrollado con **C# + WPF + .NET 8**.
Interfaz oscura con acentos rojos, barra lateral con iconos, ventana personalizada (sin bordes de Windows) y arquitectura MVVM.

## 1. Requisitos SOLO para el desarrollador

| Herramienta | Uso | Instalación |
|---|---|---|
| .NET 8 SDK | Compilar | https://dotnet.microsoft.com/download/dotnet/8.0 |
| Inno Setup 6.3+ | Generar `MineMount-Setup.exe` | https://jrsoftware.org/isdl.php |

> El usuario final **NO** necesita nada de esto: el launcher se publica como
> **Win-x64 Self-Contained** (incluye el runtime .NET dentro del .exe).

Estado en esta máquina:
- SDK .NET 8.0.425 → `C:\Users\mirro\AppData\Local\dotnet\dotnet.exe`
- Inno Setup 6 → `C:\Users\mirro\AppData\Local\Programs\Inno Setup 6\ISCC.exe`

## 2. Estructura del proyecto

```
MineMount/
├── MineMount.sln
├── build.ps1                  # Script de build completo
├── README.md
├── MineMount/                 # Proyecto WPF
│   ├── App.xaml / App.xaml.cs          # Arranque + DI (servicios/VMs)
│   ├── MainWindow.xaml / .cs           # Ventana con chrome personalizado + sidebar
│   ├── Views/                          # Vistas (UserControls)
│   │   ├── HomeView    (banner + JUGAR + noticias)
│   │   ├── GamesView   (versiones + modpacks)
│   │   ├── ServersView (lista de servidores)
│   │   └── SettingsView(cuenta, RAM, Java, launcher, perfiles)
│   ├── ViewModels/                     # MVVM (CommunityToolkit.Mvvm)
│   ├── Models/Models.cs                # LauncherSettings, MinecraftVersion, ModpackInfo...
│   ├── Services/                       # 10 interfaces + implementaciones
│   │   Navigation, Settings, Auth, Minecraft, Modpack, Server,
│   │   News, Update, Profile, Log
│   ├── Styles/                         # Colors, CommonStyles, Controls, WindowChrome
│   ├── Converters/Converters.cs
│   └── Assets/Icons/                   # Iconos vectoriales XAML + minemount.ico
├── Installer/MineMount.iss    # Script Inno Setup
├── Publish/                   # Sale del publish (MineMount.exe autocontenido)
└── Output/                    # Sale el instalador (MineMount-Setup.exe)
```

## 3. Cómo compilar MineMount.exe

### Opción A: script automático (recomendado)

```powershell
.\build.ps1
```

Ejecuta: restore → build → publish (self-contained) → genera el instalador.

### Opción B: manual (CLI)

```powershell
dotnet restore
dotnet build -c Release
dotnet publish MineMount\MineMount.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o Publish
```

Resultado: `Publish\MineMount.exe` (~73 MB, ejecutable único, no requiere .NET instalado).

### Opción C: Visual Studio

1. Abrir `MineMount.sln`
2. Configuración `Release`
3. Click derecho al proyecto → **Publish** → `win-x64`, **Self-Contained**, **Single file**

## 4. Cómo generar MineMount-Setup.exe

Con Inno Setup instalado:

```powershell
# Opción automática (incluida en build.ps1)
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" Installer\MineMount.iss

# Opción manual: abrir Installer\MineMount.iss en el IDE de Inno Setup y pulsar F9
```

Resultado: `Output\MineMount-Setup.exe` (~68 MB).

El instalador:
- Muestra el nombre MineMount y asistente moderno
- Instala en `C:\Program Files\MineMount\`
- Crea acceso directo en el escritorio (marcado por defecto)
- Crea grupo en el Menú Inicio (incluye "Desinstalar MineMount")
- Ofrece "Abrir MineMount" al finalizar
- No requiere .NET ni ninguna dependencia adicional
- Trae desinstalador integrado (preparado para actualizaciones futuras)

## 5. Cómo probar la instalación desde cero

1. **Desinstalar** (si había una versión): Panel de control → MineMount → Desinstalar,
   o ejecutar `C:\Program Files\MineMount\unins000.exe`
2. **Ejecutar** `Output\MineMount-Setup.exe` → aceptar el UAC
3. Elegir carpeta (por defecto `C:\Program Files\MineMount\`) y dejar marcado
   "Crear acceso directo en el escritorio"
4. Al finalizar, marcar **"Abrir MineMount"** → Finish
5. Verificar:
   - Ventana oscura con barra lateral y logo MINEMOUNT
   - Botón **JUGAR** con animación al pasar el mouse
   - Pestañas laterales: Inicio, Juegos, Servidores, Configuración (la activa en rojo)
   - Acceso directo en escritorio y Menú Inicio
6. **Desinstalar** para limpiar: los archivos del launcher y los accesos directos se
   eliminan; los logs en `%APPDATA%\MineMount` también.

## 6. Características implementadas

- Tema oscuro (#0D0D0D) con acento rojo (#E53935)
- Sidebar con iconos vectoriales y selección dinámica en rojo
- Ventana sin bordes: barra superior propia, minimizar/maximizar/cerrar,
  doble clic para maximizar, redimensionar, esquinas redondeadas, no tapa la barra de tareas
- Botón JUGAR grande con glow, escala y hover
- Navegación MVVM con inyección de dependencias (Microsoft.Extensions.Hosting)
- 10 servicios con interfaces, listos para crecer (login, descargas, modpacks, auto-update...)
- Registro de logs en `%APPDATA%\MineMount\minemount.log`

## 7. Próximos pasos (arquitectura preparada)

- Login real (Microsoft/Mojang) → `IAuthService`
- Descarga real de versiones de Minecraft → `IMinecraftService`
- NeoForge/Fabric → `MinecraftVersion.Modloader`
- Modpacks reales → `IModpackService`
- Servidores dinámicos → `IServerService`
- Noticias desde API → `INewsService`
- Auto-update del launcher → `IUpdateService`
- Perfiles → `IProfileService`, configuración persistente → `ISettingsService`
