# :enddoc:

warn('lib/rational.rb is deprecated') if $VERBOSE

class Integer

  alias quof fdiv
  alias rdiv quo

  alias power! ** unless method_defined? :power!
  alias rpower **

end

