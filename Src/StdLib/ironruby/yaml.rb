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

require 'stringio'
require 'date'
load_assembly 'IronRuby.Libraries.Yaml', 'IronRuby.StandardLibrary.Yaml'

require 'yaml/types'

module YAML
  # Psych's load_stream: every document, yielded one at a time when there is a
  # block and gathered into an Array when there is not (Syck answered a
  # YAML::Stream).
  def self.load_stream(io)
    documents = []
    each_document(io) { |doc| block_given? ? yield(doc) : documents << doc }
    block_given? ? nil : documents
  end
end
