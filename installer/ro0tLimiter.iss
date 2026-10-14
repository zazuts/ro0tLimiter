; English installer. Downloads the .NET 8 Desktop Runtime from Microsoft when it is missing.
; WinDivert.dll and WinDivert64.sys are shipped inside the setup, with their license.

#define MyAppName "ro0tLimiter"
#define MyAppVersion "1.0.9"
#define MyAppPublisher "Andre"
#define MyAppExeName "ro0tLimiter.exe"

[Setup]
AppId={{A7C1E5B2-4F18-4C0A-9B3E-6D2A8F0C1E44}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright=Copyright (C) 2026 Andre
AppContact=ro0t@hotmail.it
DefaultDirName={autopf}\ro0tLimiter
DefaultGroupName=ro0tLimiter
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=ro0tLimiter-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile=..\third_party\WinDivert\LICENSE
InfoBeforeFile=Before.rtf
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion=1.0.9.0
VersionInfoCompany=Andre
VersionInfoDescription=ro0tLimiter setup
VersionInfoProductName=ro0tLimiter
VersionInfoProductVersion=1.0.9.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\bin\Release\net8.0-windows\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,createdump.exe"

[Icons]
Name: "{group}\ro0tLimiter"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\ro0tLimiter"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop icon"; GroupDescription: "Additional icons:"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch ro0tLimiter"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--uninstall-service"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveService"

[Code]
function URLDownloadToFile(pCaller: Integer; URL, FileName: String; Reserved: Integer; Callback: Integer): Integer;
  external 'URLDownloadToFileW@urlmon.dll stdcall';

function DesktopRuntimeInstalled: Boolean;
var
  Names: TArrayOfString;
  I: Integer;
  Root: String;
  FindRec: TFindRec;
begin
  Result := False;
  if RegGetValueNames(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
      if Pos('8.', Names[I]) = 1 then
      begin
        Result := True;
        Exit;
      end;
  end;
  Root := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if FindFirst(Root + '\8.*', FindRec) then
  begin
    Result := True;
    FindClose(FindRec);
  end;
end;

procedure StopProduct;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\net.exe'), 'stop ro0tLimiter', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM ro0tLimiter.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(400);
end;

procedure StartProductService;
var
  Code: Integer;
begin
  if not Exec(ExpandConstant('{sys}\sc.exe'), 'query ro0tLimiter', '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Exit;
  if Code = 0 then
    Exec(ExpandConstant('{sys}\net.exe'), 'start ro0tLimiter', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Dest: String;
  Code: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    StartProductService;
    Exit;
  end;
  if CurStep <> ssInstall then
    Exit;
  StopProduct;
  if DesktopRuntimeInstalled then
  begin
    WizardForm.StatusLabel.Caption := 'The .NET 8 Desktop Runtime is already installed.';
    Exit;
  end;

  WizardForm.StatusLabel.Caption := 'Downloading the .NET 8 Desktop Runtime from Microsoft...';
  Dest := ExpandConstant('{tmp}\windowsdesktop-runtime-win-x64.exe');
  if URLDownloadToFile(0, 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe', Dest, 0, 0) <> 0 then
    RaiseException('Could not download the .NET 8 Desktop Runtime. Connect to the internet and run the setup again.');

  WizardForm.StatusLabel.Caption := 'Installing the .NET 8 Desktop Runtime...';
  if not Exec(Dest, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, Code) then
    RaiseException('Could not start the .NET 8 Desktop Runtime installer.');
  if not (Code in [0, 1638, 3010]) then
    RaiseException('The .NET 8 Desktop Runtime installer failed with code ' + IntToStr(Code) + '.');
end;
