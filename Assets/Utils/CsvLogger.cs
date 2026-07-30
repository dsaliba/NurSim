using System.Globalization;
using System.IO;
using System.Text;

namespace GazeData.Utils
{
    /// <summary>
    /// CVS Writer to save Gaze Data
    /// </summary>
    public class CsvLogger : System.IDisposable
    {
        private readonly StreamWriter _writer;
        public string FilePath { get; }

        public CsvLogger(string directory, string fileName, string headerRow)
        {
            Directory.CreateDirectory(directory);
            FilePath = Path.Combine(directory, fileName);
            _writer = new StreamWriter(FilePath, append: false, Encoding.UTF8) { AutoFlush = false };
            _writer.WriteLine(headerRow);
        }

        public void WriteRow(params object[] fields)
        {
            for (int i = 0; i < fields.Length; i++)
            {
                if (i > 0) _writer.Write(',');
                _writer.Write(Format(fields[i]));
            }
            _writer.Write('\n');
        }

        private static string Format(object value)
        {
            switch (value)
            {
                case null:
                    return string.Empty;
                case float f:
                    return f.ToString("G7", CultureInfo.InvariantCulture);
                case double d:
                    return d.ToString("G9", CultureInfo.InvariantCulture);
                case bool b:
                    return b ? "1" : "0";
                default:
                    return value.ToString();
            }
        }

    // Force all the record data/events to the server in case of disconnection
        public void Flush() => _writer.Flush(); 

    // Flush data and close/release the file handle
        public void Dispose()
        {
            _writer.Flush();
            _writer.Dispose();
        }
    }
}
