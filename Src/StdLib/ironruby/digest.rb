# ****************************************************************************
#
# Copyright (c) Microsoft Corporation. 
#
# This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
# copy of the license can be found in the License.html file at the root of this distribution. If 
# you cannot locate the  Apache License, Version 2.0, please send an email to 
# ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
# by the terms of the Apache License, Version 2.0.
#
# You must not remove this notice, or any other, from this software.
#
#
# ****************************************************************************

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Digest'

# The C# library (IronRuby.StandardLibrary.Digest) supplies the algorithm-dependent
# parts -- update/finish/reset plus block_length and digest_length, which need the
# HashAlgorithm.  Everything below is the algorithm-independent surface of
# Digest::Instance, which MRI also defines in Ruby terms.

module Digest
  module Instance
    # Abstract in MRI; Digest::Base overrides both with the C# implementation.
    # They exist here so that Digest::Instance itself answers to them, and so the
    # two really are the same method rather than two look-alike definitions.
    def update(str)
      raise RuntimeError, "#{self.class} does not implement update()"
    end
    alias << update

    # A new, reset object of the same class -- not a copy of this one's state.
    def new
      self.class.new
    end

    # Feeds the file through the digest in chunks and returns self, so that
    # Digest::MD5.new.file(path).hexdigest reads left to right.
    def file(name)
      File.open(name, "rb") do |io|
        while (chunk = io.read(16384))
          update(chunk)
        end
      end
      self
    end

    def length
      digest_length
    end
    alias size length


    # MRI compares two digests by their raw digest, and a digest against a string
    # by its hexdigest -- and insists the other side really is a String.
    def ==(other)
      return digest == other.digest if other.is_a?(Digest::Instance)
      str = String.try_convert(other)
      raise TypeError, "can't convert #{other.class} into String" if str.nil?
      to_s == str
    end

    def to_s
      hexdigest
    end

    def inspect
      "#<#{self.class.name}: #{hexdigest}>"
    end
    def base64digest(str = nil)
      # pack("m0") is documented to emit no line breaks; IronRuby's appends one,
      # so strip it rather than depend on that being fixed.
      [str ? digest(str) : digest].pack("m0").delete("\n")
    end

    def base64digest!
      [digest!].pack("m0").delete("\n")
    end
  end

  class Base
    # The C# side registers "<<" and "update" as two independent members; the
    # specs (and Method#==) require them to be one method under two names.
    alias_method :update, :<<
  end

  class Class
    def self.file(name)
      new.file(name)
    end

    def self.base64digest(str, *args)
      new(*args).base64digest(str)
    end
  end
end
