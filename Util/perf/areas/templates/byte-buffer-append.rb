# frozen_string_literal: true
# Bytes allocated by appends to a byte-backed (binary) String: run before/after Utils.Append took an exact span.
def m(n) = (3.times { yield }; a = System::GC.get_total_allocated_bytes(true); yield; printf("%-10s %dKB\n", n, (System::GC.get_total_allocated_bytes(true)-a)/1024))
bin = "\xff".b
m("bin+str") { b = ::String.new; b << bin; 3000.times { b << 'abcdefghij' } }
m("bin+bin") { b = ::String.new; b << bin; 3000.times { b << bin } }
m("bin+str+bin") { b = ::String.new; b << bin; 3000.times { b << 'abcdefghij'; b << bin } }
m("u8+str") { b = ::String.new; b << 'é'.b.force_encoding('UTF-8'); 3000.times { b << 'abcdefghij' } }
