# OpenSSL::HMAC.digest as ActiveSupport::MessageVerifier / KeyGenerator-signed cookies use it
# (SHA1 and SHA256, 64-byte key, short message), and the answers, which must equal CRuby's.
require "openssl"
N = 20_000
def cpu = Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID)
key = (0...64).map { |i| (i * 7 % 256).chr }.join.b
long = "k" * 200
p OpenSSL::HMAC.hexdigest("SHA256", key, "data"), OpenSSL::HMAC.hexdigest("SHA1", "", ""),
  OpenSSL::HMAC.hexdigest("SHA512", long, "x" * 300), OpenSSL::HMAC.hexdigest("MD5", "short", "m"),
  OpenSSL::HMAC.hexdigest("SHA3-256", key, "d")
{ "HMAC.digest SHA256" => -> { N.times { OpenSSL::HMAC.digest("SHA256", key, "--payload--") } },
  "HMAC.digest SHA1" => -> { N.times { OpenSSL::HMAC.digest("SHA1", key, "--payload--") } } }.each do |n, f|
  f.call; best = 5.times.map { t = cpu; f.call; cpu - t }.min
  printf "%-20s %7.0f ns/op\n", n, best * 1e9 / N
end
