; =====================================================================
;   IDMLite - Internet Download Manager Inno Setup Script
; =====================================================================

#define MyAppName "IDMLite"
#define MyAppVersion "1.0.1"
#define MyAppPublisher "IDMLite Project"
#define MyAppURL "https://github.com/idmlite/idmlite"
#define MyAppExeName "IDMLite.exe"
#define MyExtensionId "gdcnkipjepgidojhonbcfpgagchjcdci"

[Setup]
AppId={{C5938D2F-F96B-41E3-855A-B44670D99D41}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=..\installer\output
OutputBaseFilename=IDMLite_Setup_v{#MyAppVersion}
SetupIconFile=app_icon.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern dark
WizardImageFile=assets\wizard-side.bmp
WizardSmallImageFile=assets\wizard-small.bmp
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=yes
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#MyAppExeName}
ShowLanguageDialog=yes
DisableStartupPrompt=yes
LanguageDetectionMethod=none

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "portuguese"; MessagesFile: "compiler:Languages\Portuguese.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "arabic"; MessagesFile: "compiler:Languages\Arabic.isl"

[CustomMessages]
english.CopyingFiles=Installing files…
turkish.CopyingFiles=Dosyalar yükleniyor…
english.StartupIconDesc=Start IDMLite automatically in background on Windows startup
english.StartupGroupDesc=Startup Settings:
english.ExtensionGroup=Browser Extension (IDMLite.crx)
english.ExtensionFolder=Browser Extension Folder (Unpacked)

turkish.StartupIconDesc=Windows açıldığında IDMLite'ı arka planda otomatik başlat
turkish.StartupGroupDesc=Başlangıç Ayarları:
turkish.ExtensionGroup=Tarayıcı Eklentisi (IDMLite.crx)
turkish.ExtensionFolder=Tarayıcı Eklenti Klasörü (Unpacked)

german.StartupIconDesc=IDMLite beim Windows-Start automatisch im Hintergrund ausführen
german.StartupGroupDesc=Autostart-Einstellungen:
german.ExtensionGroup=Browser-Erweiterung (IDMLite.crx)
german.ExtensionFolder=Browser-Erweiterungsordner (Entpackt)

french.StartupIconDesc=Lancer IDMLite automatiquement en arrière-plan au démarrage de Windows
french.StartupGroupDesc=Paramètres de démarrage:
french.ExtensionGroup=Extension de navigateur (IDMLite.crx)
french.ExtensionFolder=Dossier de l'extension de navigateur (Décompressé)

spanish.StartupIconDesc=Iniciar IDMLite automáticamente en segundo plano al iniciar Windows
spanish.StartupGroupDesc=Configuración de inicio:
spanish.ExtensionGroup=Extensión de navegador (IDMLite.crx)
spanish.ExtensionFolder=Carpeta de extensión de navegador (Descomprimida)

italian.StartupIconDesc=Avvia IDMLite automaticamente in background all'avvio di Windows
italian.StartupGroupDesc=Impostazioni di avvio:
italian.ExtensionGroup=Estensione del browser (IDMLite.crx)
italian.ExtensionFolder=Cartella dell'estensione del browser (Decompressa)

portuguese.StartupIconDesc=Iniciar o IDMLite automaticamente em segundo plano ao iniciar o Windows
portuguese.StartupGroupDesc=Configurações de inicialização:
portuguese.ExtensionGroup=Extensão do navegador (IDMLite.crx)
portuguese.ExtensionFolder=Pasta da extensão do navegador (Descompactada)

russian.StartupIconDesc=Автоматически запускать IDMLite в фоновом режиме при запуске Windows
russian.StartupGroupDesc=Параметры автозагрузки:
russian.ExtensionGroup=Расширение для браузера (IDMLite.crx)
russian.ExtensionFolder=Папка расширения для браузера (Распакованная)

japanese.StartupIconDesc=Windows起動時にIDMLiteをバックグラウンドで自動起動する
japanese.StartupGroupDesc=スタートアップ設定:
japanese.ExtensionGroup=ブラウザ拡張機能 (IDMLite.crx)
japanese.ExtensionFolder=ブラウザ拡張機能フォルダー (展開済み)

arabic.StartupIconDesc=بدء تشغيل IDMLite تلقائياً في الخلفية عند بدء تشغيل Windows
arabic.StartupGroupDesc=إعدادات بدء التشغيل:
arabic.ExtensionGroup=ملحق المتصفح (IDMLite.crx)
arabic.ExtensionFolder=مجلد ملحق المتصفح (غير مضغوط)

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupicon"; Description: "{cm:StartupIconDesc}"; GroupDescription: "{cm:StartupGroupDesc}"

[Dirs]
Name: "{sd}\Temp\IDMLite"; Flags: uninsneveruninstall

[Files]
; Desktop application binaries, dependencies, and extension
Source: "dist_app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; Extension distribution packages (.crx and .zip)
Source: "dist_extension\IDMLite_Extension_v1.0.1.zip"; DestDir: "{app}\extension_dist"; Flags: ignoreversion
Source: "dist_extension\IDMLite.crx"; DestDir: "{app}\extension_dist"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:ExtensionGroup}"; Filename: "{app}\IDMLite.crx"
Name: "{group}\{cm:ExtensionFolder}"; Filename: "{app}\extension"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--minimized"; Tasks: startupicon

[Registry]
; Chromium Policy Whitelist in HKLM (Allows off-store extension in Google Chrome, Edge, Brave)

; Chromium Native Messaging Host (Google Chrome - HKLM & HKCU)
Root: HKLM; Subkey: "Software\Google\Chrome\NativeMessagingHosts\com.idmlite.nativehost"; ValueType: string; ValueData: "{app}\extension\native_host\com.idmlite.nativehost.json"; Flags: uninsdeletekey noerror
Root: HKCU; Subkey: "Software\Google\Chrome\NativeMessagingHosts\com.idmlite.nativehost"; ValueType: string; ValueData: "{app}\extension\native_host\com.idmlite.nativehost.json"; Flags: uninsdeletekey noerror

; Microsoft Edge Native Messaging Host (HKLM & HKCU)
Root: HKLM; Subkey: "Software\Microsoft\Edge\NativeMessagingHosts\com.idmlite.nativehost"; ValueType: string; ValueData: "{app}\extension\native_host\com.idmlite.nativehost.json"; Flags: uninsdeletekey noerror
Root: HKCU; Subkey: "Software\Microsoft\Edge\NativeMessagingHosts\com.idmlite.nativehost"; ValueType: string; ValueData: "{app}\extension\native_host\com.idmlite.nativehost.json"; Flags: uninsdeletekey noerror

; Brave Browser Native Messaging Host (HKLM & HKCU)
Root: HKLM; Subkey: "Software\BraveSoftware\Brave\NativeMessagingHosts\com.idmlite.nativehost"; ValueType: string; ValueData: "{app}\extension\native_host\com.idmlite.nativehost.json"; Flags: uninsdeletekey noerror
Root: HKCU; Subkey: "Software\BraveSoftware\Brave\NativeMessagingHosts\com.idmlite.nativehost"; ValueType: string; ValueData: "{app}\extension\native_host\com.idmlite.nativehost.json"; Flags: uninsdeletekey noerror

; Mozilla Firefox Native Messaging Host (HKLM & HKCU)
Root: HKLM; Subkey: "Software\Mozilla\NativeMessagingHosts\com.idmlite.nativehost"; ValueType: string; ValueData: "{app}\extension\native_host\com.idmlite.nativehost.firefox.json"; Flags: uninsdeletekey noerror
Root: HKCU; Subkey: "Software\Mozilla\NativeMessagingHosts\com.idmlite.nativehost"; ValueType: string; ValueData: "{app}\extension\native_host\com.idmlite.nativehost.firefox.json"; Flags: uninsdeletekey noerror

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
function GetFileAttributesW(lpFileName: string): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Attributes: LongWord;
begin
  Result := '';
  Attributes := GetFileAttributesW(ExpandConstant('{sd}\Temp'));
  if (Attributes <> $FFFFFFFF) and ((Attributes and $400) <> 0) then
    Result := 'The recovery storage parent cannot be a junction or symbolic link.';
  Attributes := GetFileAttributesW(ExpandConstant('{sd}\Temp\IDMLite'));
  if (Attributes <> $FFFFFFFF) and ((Attributes and $400) <> 0) then
    Result := 'The recovery storage directory cannot be a junction or symbolic link.';
end;

// Helper: Update json path in native host manifests dynamically upon install
procedure CurStepChanged(CurStep: TSetupStep);
var
  JsonFile: string;
  ExePath: string;
  UnicodeContent: string;
  AclResult: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    // Users may create their own private subdirectory, but cannot delete siblings.
    if not Exec(ExpandConstant('{sys}\icacls.exe'), '"' + ExpandConstant('{sd}\Temp\IDMLite') + '" /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-32-545:(RX,AD)"', '', SW_HIDE, ewWaitUntilTerminated, AclResult) then
      RaiseException('Could not secure the recovery storage directory.');
    if AclResult <> 0 then RaiseException('Recovery directory permissions could not be applied.');
    ExePath := ExpandConstant('{app}\{#MyAppExeName}');
    StringChangeEx(ExePath, '\', '\\', True);

    // Chrome Manifest
    JsonFile := ExpandConstant('{app}\extension\native_host\com.idmlite.nativehost.json');
    if FileExists(JsonFile) then
    begin
      UnicodeContent := '{'#13#10 +
        '  "name": "com.idmlite.nativehost",'#13#10 +
        '  "description": "IDMLite Native Messaging Host",'#13#10 +
        '  "path": "' + ExePath + '",'#13#10 +
        '  "type": "stdio",'#13#10 +
        '  "allowed_origins": ['#13#10 +
        '    "chrome-extension://{#MyExtensionId}/"'#13#10 +
        '  ]'#13#10 +
        '}';
      SaveStringToFile(JsonFile, UTF8Encode(UnicodeContent), False);
    end;

    // Firefox Manifest
    JsonFile := ExpandConstant('{app}\extension\native_host\com.idmlite.nativehost.firefox.json');
    if FileExists(JsonFile) then
    begin
      UnicodeContent := '{'#13#10 +
        '  "name": "com.idmlite.nativehost",'#13#10 +
        '  "description": "IDMLite Native Messaging Host for Firefox",'#13#10 +
        '  "path": "' + ExePath + '",'#13#10 +
        '  "type": "stdio",'#13#10 +
        '  "allowed_extensions": ['#13#10 +
        '    "idmlite@extension"'#13#10 +
        '  ]'#13#10 +
        '}';
      SaveStringToFile(JsonFile, UTF8Encode(UnicodeContent), False);
    end;

  end;
end;

const OwnedPolicies = 'Software\IDMLite\ManagedBrowserPolicy';

procedure RemoveChromiumForce(Name, PolicyPath: string);
var
  Slot, Expected, Current: string;
begin
  if RegQueryStringValue(HKLM64, OwnedPolicies, Name + '.slot', Slot) and
     RegQueryStringValue(HKLM64, OwnedPolicies, Name + '.value', Expected) and
     RegQueryStringValue(HKLM64, PolicyPath, Slot, Current) and (Current = Expected) then
    RegDeleteValue(HKLM64, PolicyPath, Slot);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Expected, Current, Remaining: string;
  FirefoxPath: string;
begin
  if CurUninstallStep <> usUninstall then Exit;
  RemoveChromiumForce('Chrome', 'Software\Policies\Google\Chrome\ExtensionInstallForcelist');
  RemoveChromiumForce('Brave', 'Software\Policies\BraveSoftware\Brave\ExtensionInstallForcelist');
  FirefoxPath := 'Software\Policies\Mozilla\Firefox';
  if RegQueryStringValue(HKLM64, OwnedPolicies, 'Firefox.written', Expected) and
     RegQueryMultiStringValue(HKLM64, FirefoxPath, 'ExtensionSettings', Current) and
     RegQueryStringValue(HKLM64, OwnedPolicies, 'Firefox.remaining', Remaining) then
  begin
    if (Current = Expected) or (Current = Expected + #0) or (Current = Expected + #0#0) then
    begin
      if Remaining = '' then RegDeleteValue(HKLM64, FirefoxPath, 'ExtensionSettings')
      else RegWriteMultiStringValue(HKLM64, FirefoxPath, 'ExtensionSettings', Remaining);
    end;
  end;
  RegDeleteKeyIncludingSubkeys(HKLM64, OwnedPolicies);
end;

#include "wizard_visual.iss"


