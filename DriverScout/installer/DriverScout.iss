; Установщик DriverScout (Inno Setup 6).
; Собирается скриптом installer\build.ps1 (публикация, подпись, упаковка)
; → installer\Output\DriverScout-Setup-<версия>.exe

#define AppName "DriverScout"
#define AppExe "DriverScout.exe"
#define AppVersion GetVersionNumbersString("..\publish\" + AppExe)

[Setup]
AppId={{1787BA3C-3D0D-4D97-88EC-D1AF48B67175}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=DriverScout
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Драйверы ставятся с правами администратора, поэтому и программа ставится для всех пользователей.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
SetupIconFile=..\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir=Output
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Установщик {#AppName}
#ifdef Sign
; Подпись установщика и деинсталлятора: команду "dsign" передаёт build.ps1 через ISCC /S.
SignTool=dsign
SignedUninstaller=yes
#endif

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Скачанные драйверы лежат в %LOCALAPPDATA%\DriverScout\Downloads у каждого пользователя и не удаляются;
; в папке установки только exe.
Type: dirifempty; Name: "{app}"
