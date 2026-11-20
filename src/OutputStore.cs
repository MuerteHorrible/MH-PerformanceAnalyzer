using System;
using System.Collections.Generic;
using System.IO;

namespace MHPerformanceAnalyzer
{
    /// <summary>
    /// The only class in Performance Analyzer that touches the file system.
    ///
    /// Every path is relative to the mod's own Output folder (&lt;mod folder&gt;\Output) and is checked before use:
    /// absolute paths, ".." and anything that resolves outside Output are refused with an exception. Performance Analyzer
    /// writes its settings file and its recording sessions here, reads back only those files, and deletes
    /// only session folders it created itself. Nothing else on the computer is read, listed or written.
    /// </summary>
    internal sealed class OutputStore
    {
        public const string FolderName = "Output";

        /// <summary>Full path of the Output folder. Never written into any log or report.</summary>
        private readonly string root;

        public OutputStore(string modDirectory)
        {
            root = Path.GetFullPath(Path.Combine(modDirectory, FolderName));
        }

        /// <summary>Creates the Output folder if it is missing (it ships with the mod, so normally it exists).</summary>
        public void EnsureRoot()
        {
            Directory.CreateDirectory(root);
        }

        /// <summary>Turns a path relative to Output into a full path, refusing anything that would leave Output.</summary>
        private string Resolve(string relative)
        {
            if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Contains(".."))
            {
                throw new ArgumentException("Performance Analyzer only uses paths inside its Output folder.", nameof(relative));
            }
            string full = Path.GetFullPath(Path.Combine(root, relative));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Performance Analyzer only uses paths inside its Output folder.", nameof(relative));
            }
            return full;
        }

        public bool FileExists(string relative) => File.Exists(Resolve(relative));

        public string? ReadText(string relative)
        {
            string path = Resolve(relative);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        /// <summary>Replaces a file in one step (written to a temporary file next to it first).</summary>
        public void WriteText(string relative, string text)
        {
            string path = Resolve(relative);
            string temp = Resolve(relative + ".tmp");
            File.WriteAllText(temp, text);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(temp, path);
        }

        public void AppendText(string relative, string text)
        {
            File.AppendAllText(Resolve(relative), text);
        }

        public void CreateDirectory(string relative)
        {
            Directory.CreateDirectory(Resolve(relative));
        }

        public bool DirectoryExists(string relative) => Directory.Exists(Resolve(relative));

        /// <summary>Names (not paths) of the folders directly inside Output.</summary>
        public List<string> ListDirectories()
        {
            var names = new List<string>();
            foreach (string dir in Directory.GetDirectories(root))
            {
                names.Add(Path.GetFileName(dir));
            }
            return names;
        }

        /// <summary>Renames a folder directly inside Output.</summary>
        public void RenameDirectory(string fromRelative, string toRelative)
        {
            Directory.Move(Resolve(fromRelative), Resolve(toRelative));
        }

        /// <summary>
        /// Deletes a Performance Analyzer session folder: only its plain files, then the empty folder. A folder that holds
        /// subfolders, or that is a link to somewhere else, is left alone.
        /// </summary>
        public bool DeleteSessionDirectory(string relative)
        {
            string dir = Resolve(relative);
            if (!Directory.Exists(dir) || IsLink(dir) || Directory.GetDirectories(dir).Length > 0)
            {
                return false;
            }
            foreach (string file in Directory.GetFiles(dir))
            {
                if (IsLink(file))
                {
                    return false;
                }
            }
            foreach (string file in Directory.GetFiles(dir))
            {
                File.Delete(file);
            }
            Directory.Delete(dir, false);
            return true;
        }

        /// <summary>A file:// address of the Output folder itself, used only when the player clicks "Open Output folder".</summary>
        public string RootUri() => new Uri(root + Path.DirectorySeparatorChar).AbsoluteUri;

        /// <summary>A file:// address for a file or folder inside Output, used only when the player clicks "Open report".</summary>
        public string FileUri(string relative) => new Uri(Resolve(relative)).AbsoluteUri;

        private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }
}
