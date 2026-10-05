"""Fast source/Win32-path models, not a Windows setup execution test.

NSIS GetFullPathName may fail on missing paths; Win32 GetFullPathNameW does
not require existence. FileFunc.GetParent removes the drive-root separator.
Primary contracts:
https://nsis.sourceforge.io/Reference/GetFullPathName
https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-getfullpathnamew
https://learn.microsoft.com/windows/win32/fileio/naming-a-file
"""
import ntpath
from pathlib import Path
import unittest


SOURCE = Path(__file__).with_name("windows-installer.nsi").read_text()
DIRECTORY = SOURCE.split("Function CheckInstallDirectory\n", 1)[1].split("FunctionEnd", 1)[0]


def ancestor_queries(path):
    """Model FileFunc.GetParent with the installer's explicit root repair/stop."""
    root = ntpath.splitdrive(path)[0] + "\\"
    current = path
    while True:
        yield current
        if current.casefold() == root.casefold():
            return
        # FileFunc.GetParent strips trailing slashes, then the last component;
        # unlike ntpath.dirname, it returns C: for the parent of C:\Users.
        current = current.rstrip("\\").rsplit("\\", 1)[0]
        if (current + "\\").casefold() == root.casefold():
            current = root
        if not current:
            raise ValueError("No root")


def check_ancestors(path, attributes):
    """Model the existing fail-closed GetFileAttributes branches.

    Fixture values are (attributes, error). Missing entries are absent paths.
    No filesystem data or Windows runtime behavior is simulated beyond this
    explicitly documented contract.
    """
    for item in ancestor_queries(path):
        flags, error = attributes.get(item, (-1, 3))
        if flags == -1:
            if error not in (2, 3):
                return False
        elif flags & 0x400 or not flags & 0x10:
            return False
    return True


class WindowsInstallDirectoryTests(unittest.TestCase):
    def test_normalization_uses_non_existence_checking_win32_api(self):
        before_parents = DIRECTORY.split("check_parent:", 1)[0]
        self.assertIn('GetFullPathNameW(w "$INSTDIR", i ${NSIS_MAX_STRLEN}, w .r0, p 0)', before_parents)
        self.assertNotIn('GetFullPathName $INSTDIR', before_parents)
        self.assertIn('${If} $1 == 0', before_parents)
        self.assertIn('${If} $1 >= ${NSIS_MAX_STRLEN}', before_parents)
        self.assertLess(before_parents.index('GetFullPathNameW('), before_parents.index('StrCpy $INSTDIR "$0"'))
        self.assertNotIn('CreateDirectory', DIRECTORY)
        self.assertNotIn('SetOutPath', DIRECTORY)

    def test_source_repairs_root_before_query_and_stops_after_root(self):
        parents = DIRECTORY.split('check_parent:', 1)[1].split('checked_parents:', 1)[0]
        self.assertIn('${GetRoot} "$INSTDIR" $InstallRoot', DIRECTORY)
        self.assertIn('StrCpy $InstallRoot "$InstallRoot\\"', DIRECTORY)
        self.assertLess(parents.index('GetFileAttributesW('), parents.index('${If} $0 == $InstallRoot'))
        self.assertLess(parents.index('${If} $0 == $InstallRoot'), parents.index('${GetParent}'))
        self.assertIn('${If} "$1\\" == $InstallRoot\n      StrCpy $1 "$InstallRoot"', parents)
        self.assertIn('Goto checked_parents', parents)

    def test_fresh_default_path_checks_all_ancestors_through_drive_root(self):
        path = r'C:\Users\New User\AppData\Local\Programs\MusicMachine'
        expected = [path, r'C:\Users\New User\AppData\Local\Programs',
                    r'C:\Users\New User\AppData\Local', r'C:\Users\New User\AppData',
                    r'C:\Users\New User', r'C:\Users', 'C:\\']
        self.assertEqual(list(ancestor_queries(path)), expected)
        # Programs and MusicMachine need not already exist on a fresh profile.
        existing = {item: (0x10, 0) for item in expected[2:]}
        self.assertTrue(check_ancestors(path, existing))
        self.assertNotIn('C:', expected)

    def test_empty_existing_custom_path_and_trailing_separator(self):
        for path in (r'D:\Apps\Music Machine', 'D:\\Apps\\Music Machine\\'):
            with self.subTest(path=path):
                queries = list(ancestor_queries(path))
                self.assertEqual(queries[-1], 'D:\\')
                self.assertEqual(len(queries), 3)
                self.assertTrue(check_ancestors(path, {item: (0x10, 0) for item in queries}))

    def test_drive_root_is_checked_once(self):
        self.assertEqual(list(ancestor_queries('C:\\')), ['C:\\'])

    def test_missing_destination_does_not_bypass_link_file_or_access_denial(self):
        path = r'C:\Users\Person\AppData\Local\Programs\MusicMachine'
        ancestor = r'C:\Users\Person'
        for value in ((0x410, 0), (0x20, 0), (-1, 5), (-1, 123)):
            with self.subTest(attributes=value):
                self.assertFalse(check_ancestors(path, {ancestor: value}))
        for error in (2, 3):
            self.assertTrue(check_ancestors(path, {ancestor: (-1, error)}))

    def test_source_keeps_content_ownership_and_link_protection(self):
        section = SOURCE.split('Section "MusicMachine"', 1)[1]
        self.assertLess(section.index('Call CheckInstallDirectory'), section.index('SetOverwrite on'))
        for marker in ('.git', '*.csproj', '*.sln', '*.slnx'):
            self.assertIn(marker, DIRECTORY)
        for evidence in ('Call CheckInstallTree', 'ReadRegStr $0 HKCU',
                         'MusicMachine.Desktop.exe', 'release.json', 'Uninstall.exe',
                         'MusicMachine per-user Windows installation v1', '$0.update-$2'):
            self.assertIn(evidence, DIRECTORY)
        self.assertIn('IntOp $2 $1 & 0x400', DIRECTORY)
        self.assertIn('IntOp $5 $4 & 0x400', SOURCE)
        self.assertIn('${If} $2 != 2\n      ${AndIf} $2 != 3', DIRECTORY)

    def test_refusal_explains_target_reason_and_action(self):
        refusal = SOURCE.split('Function RefuseInstallDirectory\n', 1)[1].split('FunctionEnd', 1)[0]
        self.assertIn('$INSTDIR', refusal)
        self.assertIn('$InstallDirectoryIssue', refusal)
        self.assertIn('DetailPrint', refusal)
        self.assertIn('No files have been replaced.', refusal)
        for diagnostic in ('source-code files', 'not a recognized MusicMachine installation',
                           'linked folder', 'A file is blocking', 'error $2', 'too long'):
            self.assertIn(diagnostic, DIRECTORY)


if __name__ == '__main__':
    unittest.main()
