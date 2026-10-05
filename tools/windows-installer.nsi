Unicode true
!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!define PRODUCT "MusicMachine"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\MusicMachine"
Name "${PRODUCT}"
OutFile "${OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\MusicMachine"
InstallDirRegKey HKCU "${UNINSTALL_KEY}" "InstallLocation"
RequestExecutionLevel user
SetCompressor /SOLID lzma
CRCCheck force
ManifestDPIAware true
VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "MusicMachine"
VIAddVersionKey "FileDescription" "MusicMachine Setup"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "MIT License"
!define MUI_ICON "${ICON}"
!define MUI_UNICON "${ICON}"
!define MUI_ABORTWARNING
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "${LICENSE_FILE}"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\MusicMachine.Desktop.exe"
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

!macro CheckAppClosed
  ${If} ${FileExists} "$INSTDIR\MusicMachine.Desktop.exe"
    System::Call 'kernel32::CreateFileW(w "$INSTDIR\MusicMachine.Desktop.exe", i 0x40000000, i 0, p 0, i 3, i 0, p 0) p .r0'
    ${If} $0 == -1
      MessageBox MB_OK|MB_ICONSTOP "Close MusicMachine before installing or uninstalling it." /SD IDOK
      SetErrorLevel 2
      Abort
    ${EndIf}
    System::Call 'kernel32::CloseHandle(p r0)'
  ${EndIf}
!macroend
Var CheckedEntries

Function RefuseInstallDirectory
  MessageBox MB_OK|MB_ICONSTOP "Choose a new or empty folder, or an existing MusicMachine installation. Source checkouts, unrelated files and linked folders will not be overwritten." /SD IDOK
  SetErrorLevel 2
  Abort
FunctionEnd

Function CheckInstallTree
  ; Inspect without following links. WIN32_FIND_DATAW is 592 bytes, filename at
  ; offset 44. Capture GetLastError in the same System call, before NSIS can
  ; change it, so access-denied never looks like an empty directory.
  Exch $0
  Push $1
  Push $2
  Push $3
  Push $4
  Push $5
  Push $6
  StrCpy $1 -1
  StrCpy $6 0
  System::Alloc 592
  Pop $3
  ${If} $3 == 0
    Goto tree_refused
  ${EndIf}
  System::Call 'kernel32::FindFirstFileW(w "$0\*", p r3) p .r1 ?e'
  Pop $4
  ${If} $1 == -1
    ${If} $4 == 2
      Goto tree_cleanup
    ${EndIf}
    Goto tree_refused
  ${EndIf}
  tree_next:
    IntOp $5 $3 + 44
    System::Call '*$5(&w260 .r2)'
    ${If} $2 != "."
    ${AndIf} $2 != ".."
      IntOp $CheckedEntries $CheckedEntries + 1
      ${If} $CheckedEntries > 20000
        Goto tree_refused
      ${EndIf}
      System::Call '*$3(i .r4)'
      IntOp $5 $4 & 0x400
      ${If} $5 != 0
        Goto tree_refused
      ${EndIf}
      IntOp $4 $4 & 0x10
      ${If} $4 != 0
        Push "$0\$2"
        Call CheckInstallTree
        ${If} ${Errors}
          Goto tree_refused
        ${EndIf}
      ${EndIf}
    ${EndIf}
    System::Call 'kernel32::FindNextFileW(p r1, p r3) i .r4 ?e'
    Pop $5
    ${If} $4 != 0
      Goto tree_next
    ${EndIf}
    ${If} $5 == 18
      Goto tree_cleanup
    ${EndIf}
  tree_refused:
    StrCpy $6 1
  tree_cleanup:
    ${If} $1 != -1
      System::Call 'kernel32::FindClose(p r1)'
    ${EndIf}
    ${If} $3 != 0
      System::Free $3
    ${EndIf}
    ClearErrors
    ${If} $6 == 1
      SetErrors
    ${EndIf}
  Pop $6
  Pop $5
  Pop $4
  Pop $3
  Pop $2
  Pop $1
  Pop $0
FunctionEnd

Function CheckInstallDirectory
  GetFullPathName $INSTDIR "$INSTDIR"
  ${If} $INSTDIR == ""
    Call RefuseInstallDirectory
  ${EndIf}
  ; A reparse point in any ancestor could redirect writes outside this folder.
  StrCpy $0 "$INSTDIR"
  check_parent:
    System::Call 'kernel32::GetFileAttributesW(w r0) i .r1 ?e'
    Pop $2
    ${If} $1 == -1
      ${If} $2 != 2
      ${AndIf} $2 != 3
        Call RefuseInstallDirectory
      ${EndIf}
    ${Else}
      IntOp $2 $1 & 0x400
      IntOp $1 $1 & 0x10
      ${If} $2 != 0
      ${OrIf} $1 == 0
        Call RefuseInstallDirectory
      ${EndIf}
    ${EndIf}
    ${GetParent} "$0" $1
    ${If} $1 != ""
    ${AndIf} $1 != $0
      StrCpy $0 "$1"
      Goto check_parent
    ${EndIf}
  ; These markers must never be accepted even if a stale registry entry exists.
  System::Call 'kernel32::GetFileAttributesW(w "$INSTDIR\.git") i .r0'
  ${If} $0 != -1
    Goto refuse_directory
  ${EndIf}
  IfFileExists "$INSTDIR\*.csproj" refuse_directory
  IfFileExists "$INSTDIR\*.sln" refuse_directory
  IfFileExists "$INSTDIR\*.slnx" refuse_directory
  System::Call 'kernel32::GetFileAttributesW(w "$INSTDIR") i .r0 ?e'
  Pop $1
  ${If} $0 == -1
    ${If} $1 == 2
    ${OrIf} $1 == 3
      Return
    ${EndIf}
    Goto refuse_directory
  ${EndIf}
  StrCpy $CheckedEntries 0
  Push "$INSTDIR"
  Call CheckInstallTree
  ${If} ${Errors}
    Goto refuse_directory
  ${EndIf}
  ${If} $CheckedEntries == 0
    Return
  ${EndIf}
  ${IfNot} ${FileExists} "$INSTDIR\MusicMachine.Desktop.exe"
    Goto refuse_directory
  ${EndIf}
  ${IfNot} ${FileExists} "$INSTDIR\release.json"
    Goto refuse_directory
  ${EndIf}
  ${IfNot} ${FileExists} "$INSTDIR\Uninstall.exe"
    Goto refuse_directory
  ${EndIf}
  ClearErrors
  ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "InstallLocation"
  ${IfNot} ${Errors}
    GetFullPathName $0 "$0"
    ${If} $0 == $INSTDIR
      Return
    ${EndIf}
  ${EndIf}
  ClearErrors
  FileOpen $1 "$INSTDIR\.installer-owned" r
  ${IfNot} ${Errors}
    FileRead $1 $2 128
    FileClose $1
    ${If} $2 == "MusicMachine per-user Windows installation v1"
      Return
    ${EndIf}
  ${EndIf}
  refuse_directory:
    Call RefuseInstallDirectory
FunctionEnd

Function .onInit
  SetShellVarContext current
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "MusicMachine requires 64-bit Windows." /SD IDOK
    SetErrorLevel 2
    Quit
  ${EndIf}
FunctionEnd
Section "MusicMachine"
  Call CheckInstallDirectory
  !insertmacro CheckAppClosed
  ClearErrors
  SetOverwrite on
  !include "${INSTALL_FILES}"
  SetOutPath "$INSTDIR"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  FileOpen $0 "$INSTDIR\.installer-owned" w
  FileWrite $0 "MusicMachine per-user Windows installation v1"
  FileClose $0
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONSTOP "Installation failed. Close MusicMachine and try again." /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "MusicMachine"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "Isaiah Pettingill"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\musicmachine.ico"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "URLInfoAbout" "https://github.com/isaiahpettingill/MusicMachine"
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" ${SIZE_KB}
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  CreateShortcut "$SMPROGRAMS\MusicMachine.lnk" "$INSTDIR\MusicMachine.Desktop.exe" "" "$INSTDIR\musicmachine.ico"
  ${If} ${Errors}
    SetErrorLevel 2
    Abort
  ${EndIf}
  SetErrorLevel 0
SectionEnd
Function un.onInit
  SetShellVarContext current
FunctionEnd
Section "Uninstall"
  !insertmacro CheckAppClosed
  ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "InstallLocation"
  ${If} $0 == $INSTDIR
    Delete "$SMPROGRAMS\MusicMachine.lnk"
    DeleteRegKey HKCU "${UNINSTALL_KEY}"
  ${EndIf}
  ; Delete only files owned by this package. User songs are never recursively removed.
  !include "${UNINSTALL_FILES}"
  Delete "$INSTDIR\Uninstall.exe"
  Delete "$INSTDIR\.installer-owned"
  RMDir "$INSTDIR"
  SetErrorLevel 0
SectionEnd
