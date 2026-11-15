; ThrottleView one-click installer for Inno Setup (https://jrsoftware.org/isinfo.php).
;
; Produces ThrottleViewSetup-<version>-release.exe in the Output folder above the repository. ThrottleView is a user-mode app that writes only HKCU and
; %APPDATA%, so the installer runs without administrator rights (PrivilegesRequired=lowest). It
; installs the executable, adds a per-user startup entry that launches into the tray, and starts the
; app. Uninstall stops the app and removes the startup entry; recorded history under
; %APPDATA%\ThrottleView is left in place.
;
; To build: open ThrottleView.sln in Visual Studio and build (Release|x64 preferred, Debug|x64 also
; works) so ThrottleView.exe exists, then open this file in Inno Setup and press Compile.

#define MyAppName "ThrottleView"
#define MyAppPublisher "PlexPixel"
#define MyAppExe "ThrottleView.exe"

; Pick whichever configuration you actually built. Release is preferred, but Visual Studio builds
; Debug by default, so if only the Debug output exists the installer uses that and still compiles.
#if FileExists(AddBackslash(SourcePath) + "ThrottleView\bin\x64\Release\" + MyAppExe)
  #define Config "Release"
#elif FileExists(AddBackslash(SourcePath) + "ThrottleView\bin\x64\Debug\" + MyAppExe)
  #define Config "Debug"
#else
  #define Config "Release"
#endif

; AppVersion is read from the built exe, which takes it from AssemblyVersion in
; ThrottleView\Properties\AssemblyInfo.cs. The fourth number is dropped.
#define VerMajor
#define VerMinor
#define VerRev
#define VerBuild
#expr GetVersionComponents(AddBackslash(SourcePath) + "ThrottleView\bin\x64\" + Config + "\" + MyAppExe, VerMajor, VerMinor, VerRev, VerBuild)
#define MyAppVersion Str(VerMajor) + "." + Str(VerMinor) + "." + Str(VerRev)

[Setup]
AppId={{B2D4F6A8-1C3E-4A5B-8D7F-2E4C6A8B0D1F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\{#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputDir=..\Output
OutputBaseFilename=ThrottleViewSetup-{#MyAppVersion}-release
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64
ArchitecturesAllowed=x64
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; Uses the {#Config} build selected above (Release if present, otherwise Debug).
Source: "ThrottleView\bin\x64\{#Config}\{#MyAppExe}"; DestDir: "{app}"; Flags: ignoreversion
; The GPLv3 text is installed with the program.
Source: "LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
; Optional sensor library for accurate CPU/GPU temperatures. Bundled only if you placed it next to
; the exe before compiling. ThrottleView loads it at runtime if present, otherwise it falls back to
; the WMI thermal zone.
Source: "ThrottleView\bin\x64\{#Config}\LibreHardwareMonitorLib.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Registry]
; Start with Windows, launching straight into the tray so history recording is continuous.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "ThrottleView"; ValueData: """{app}\{#MyAppExe}"" --tray"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "Launch ThrottleView"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c taskkill /im {#MyAppExe} /f"; Flags: runhidden; RunOnceId: "StopThrottleView"
