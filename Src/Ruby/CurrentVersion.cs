// Hand-maintained. The legacy build generated this from Build/Templates plus
// CurrentVersion.props; the modern SDK build has no such step.
using System;

namespace IronRuby {
    public static class CurrentVersion {
        public const int Major = 1;
        public const int Minor = 2;
        public const int Micro = 0;
        public const string ReleaseLevel = "dev";
        public const int ReleaseSerial = 0;

        public const string ShortReleaseLevel = "dev";

        public const string Series = "1.2";
        public const string DisplayVersion = "1.2.0-dev";
        public const string DisplayName = "IronRuby 1.2.0-dev";

        public const string AssemblyVersion = "1.2.0.0";

        public const string AssemblyFileVersion = "1.2.0.0";
        public const string AssemblyInformationalVersion = "IronRuby 1.2.0 dev 0";

        public static readonly Version Version = new Version(Major, Minor, Micro);
    }
}
