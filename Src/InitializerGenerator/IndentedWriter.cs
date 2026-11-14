using System.Text;

namespace IronRuby.InitializerGenerator {
    /// <summary>
    /// System.CodeDom.Compiler.IndentedTextWriter as far as the generator uses it (the indentation is written
    /// when a line gets its first text, and an empty WriteLine counts as text), so that the output is what
    /// ClassInitGenerator wrote.
    /// </summary>
    internal sealed class IndentedWriter {
        private const string TabString = "    ";

        private readonly StringBuilder _text = new StringBuilder();
        private int _indent;
        private bool _tabsPending;

        public int Indent {
            get { return _indent; }
            set { _indent = value < 0 ? 0 : value; }
        }

        private void OutputTabs() {
            if (_tabsPending) {
                for (int i = 0; i < _indent; i++) {
                    _text.Append(TabString);
                }
                _tabsPending = false;
            }
        }

        /// <summary>
        /// Text that is not part of the indented output (the file header).
        /// </summary>
        public void WriteRaw(string text) {
            _text.Append(text);
        }

        public void Write(string text) {
            OutputTabs();
            _text.Append(text);
        }

        public void Write(string format, params object[] args) {
            OutputTabs();
            _text.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, format, args);
        }

        public void WriteLine() {
            OutputTabs();
            _text.Append('\n');
            _tabsPending = true;
        }

        public void WriteLine(string text) {
            OutputTabs();
            _text.Append(text);
            _text.Append('\n');
            _tabsPending = true;
        }

        public void WriteLine(string format, params object[] args) {
            OutputTabs();
            _text.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, format, args);
            _text.Append('\n');
            _tabsPending = true;
        }

        public override string ToString() {
            return _text.ToString();
        }
    }
}
