// Hand-maintained. The legacy build generated this from Build/Templates plus
// CurrentVersion.props; the modern SDK build has no such step.
//
// Major.Minor is the Ruby version IronRuby implements: IronRuby 4.0 is Ruby 4.0, and
// moves to 4.1 when RUBY_VERSION does. Micro counts IronRuby's own releases for that
// Ruby version; it is not CRuby's patch level.
using System;

namespace IronRuby {
    public static class CurrentVersion {
        public const int Major = 4;
        public const int Minor = 0;
        public const int Micro = 0;
        public const string ReleaseLevel = "final";
        public const int ReleaseSerial = 0;

        public const string ShortReleaseLevel = "final";

        public const string Series = "4.0";
        public const string DisplayVersion = "4.0.0";
        public const string DisplayName = "IronRuby 4.0.0";

        public const string AssemblyVersion = "4.0.0.0";

        public const string AssemblyFileVersion = "4.0.0.0";
        public const string AssemblyInformationalVersion = "IronRuby 4.0.0";

        public static readonly Version Version = new Version(Major, Minor, Micro);
    }
}
