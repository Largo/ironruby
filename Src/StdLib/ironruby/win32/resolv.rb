# Win32::Resolv over System.Net.NetworkInformation: the two things resolv.rb asks
# for on Windows, the hosts file (Resolv::Hosts) and the DNS servers and search
# suffixes (Resolv::DNS::Config).  CRuby reads them from the registry and
# GetNetworkParams; the 1.9 version did the same over dl, which IronRuby never had,
# so its require failed and resolv ran with no hosts file and no nameservers.
module Win32
  module Resolv
    load_assembly 'System.Net.NetworkInformation'

    def self.get_hosts_path
      dir = File.join(System::Environment.system_directory.to_s, "drivers", "etc")
      path = File.expand_path("hosts", dir)
      File.exist?(path) ? path : nil
    rescue Exception
      nil
    end

    # [search, nameserver] the way CRuby returns them: each an Array, or nil when empty.
    def self.get_resolv_info
      search, nameserver = get_info
      search.delete("")
      search.uniq!
      nameserver.delete("")
      nameserver.delete("0.0.0.0")
      nameserver.uniq!
      [search.empty? ? nil : search, nameserver.empty? ? nil : nameserver]
    end

    def self.get_info # :nodoc:
      search = []
      nameserver = []
      ni = System::Net::NetworkInformation
      begin
        domain = ni::IPGlobalProperties.get_ip_global_properties.domain_name.to_s
        search << domain unless domain.empty?
      rescue Exception
      end
      interfaces = begin
        ni::NetworkInterface.get_all_network_interfaces.to_a
      rescue Exception
        []
      end
      interfaces.each do |nic|
        next unless nic.operational_status.to_s == "Up"
        next if nic.network_interface_type.to_s == "Loopback"
        props = begin
          nic.get_ip_properties
        rescue Exception
          next
        end
        suffix = props.dns_suffix.to_s
        search << suffix unless suffix.empty?
        props.dns_addresses.each do |address|
          # A scoped (link-local) address is only reachable through its own
          # interface, which Resolv::DNS has no way to name; skip it.
          text = address.to_s
          nameserver << text unless text.include?("%")
        end
      end
      [search, nameserver]
    end
    private_class_method :get_info
  end
end
