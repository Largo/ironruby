require 'net/http'
require 'rubygems/remote_fetcher'

module Gem::GemcutterUtilities

  include Gem::Text

  def sign_in
    return if Gem.configuration.rubygems_api_key

    say "Enter your RubyGems.org credentials."
    say "Don't have an account yet? Create one at http://rubygems.org/sign_up"

    email    =              ask "   Email: "
    password = ask_for_password "Password: "
    say "\n"

    response = rubygems_api_request :get, "api/v1/api_key" do |request|
      request.basic_auth email, password
    end

    with_response response do |resp|
      say "Signed in."
      Gem.configuration.rubygems_api_key = resp.body
    end
  end

  def rubygems_api_request(method, path, &block)
    host = ENV['RUBYGEMS_HOST'] || 'https://rubygems.org'
    uri = URI.parse "#{host}/#{path}"

    request_method = Net::HTTP.const_get method.to_s.capitalize

    Gem::RemoteFetcher.fetcher.request(uri, request_method, &block)
  end

  # The response body comes from the server and is sanitised before it is
  # shown (backported from RubyGems 3.0.3, CVE-2019-8323).

  def with_response(resp, error_prefix = nil)
    case resp
    when Net::HTTPSuccess then
      if block_given? then
        yield resp
      else
        say clean_text(resp.body)
      end
    else
      message = resp.body
      message = "#{error_prefix}: #{message}" if error_prefix

      say clean_text(message)
      terminate_interaction 1
    end
  end

end
