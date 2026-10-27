# Win32::Resolv, which resolv.rb asks for on Windows: where the hosts file is, and the
# DNS servers and search domains.  CRuby's reads them through a C extension
# (win32/resolv.so) and the registry; there is no copy of it under ruby/4.0, so the
# lookup used to fall through to the 1.9.1 version, built on the long-gone DL
# library.  .NET's System.Net.NetworkInformation answers the same questions.

load_assembly 'System.Net.NetworkInformation'
load_assembly 'System.Net.Primitives'

module Win32
  module Resolv
    # CRuby raises this for a failed Win32 call; nothing here makes one.
    class Error < StandardError
      def initialize(code, message)
        super(message)
        @code = code
      end

      attr_reader :code
    end

    def self.get_hosts_path
      path = ::File.join(::System::Environment.SystemDirectory.to_s.tr("\\", "/"), "drivers/etc/hosts")
      ::File.exist?(path) ? path : nil
    end

    # [search domains, name servers], each nil when there are none.
    def self.get_resolv_info
      search = []
      nameserver = []
      information = ::System::Net::NetworkInformation
      domain = information::IPGlobalProperties.GetIPGlobalProperties.DomainName.to_s
      search << domain unless domain.empty?
      information::NetworkInterface.GetAllNetworkInterfaces.each do |interface|
        next unless interface.OperationalStatus == information::OperationalStatus.Up
        next if interface.NetworkInterfaceType == information::NetworkInterfaceType.Loopback
        properties = interface.GetIPProperties
        suffix = properties.DnsSuffix.to_s
        search << suffix unless suffix.empty?
        properties.DnsAddresses.each do |address|
          # fec0:0:0:ffff::1-3 are Windows' placeholders for "no IPv6 DNS server configured"
          next if address.IsIPv6SiteLocal
          nameserver << address.ToString.to_s.sub(/%\d+\z/, "")
        end
      end
      search.uniq!
      nameserver.delete("0.0.0.0")
      nameserver.uniq!
      [search.empty? ? nil : search, nameserver.empty? ? nil : nameserver]
    end
  end
end
