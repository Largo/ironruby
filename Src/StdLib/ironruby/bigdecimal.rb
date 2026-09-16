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

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.BigDecimal'

class BigDecimal
  NAN = BigDecimal("NaN")
  INFINITY = BigDecimal("Infinity")

  # A BigDecimal is immutable, so there is nothing for #dup to copy. MRI defines the two as
  # one method, which is what the spec checks with instance_method(:clone) == instance_method(:dup).
  def dup
    self
  end
  alias_method :clone, :dup
end
