# ns per iterator call over a short Array, from a method; least of 5 rounds
def t_each(a, n);  i = 0; s = 0; while i < n; a.each { |x| s += x }; i += 1; end; s; end
def t_any(a, n);   i = 0; c = 0; while i < n; c += 1 if a.any? { |x| x > 1000 }; i += 1; end; c; end
def t_all(a, n);   i = 0; c = 0; while i < n; c += 1 if a.all? { |x| x < 1000 }; i += 1; end; c; end
def t_map(a, n);   i = 0; while i < n; a.map { |x| x }; i += 1; end; end
def t_ewi(a, n);   i = 0; s = 0; while i < n; a.each_with_index { |x, j| s += j }; i += 1; end; s; end
def t_inject(a, n);i = 0; s = 0; while i < n; s += a.inject(0) { |m, x| m + x }; i += 1; end; s; end
def t_sum(a, n);   i = 0; s = 0; while i < n; s += a.sum; i += 1; end; s; end
def t_pushpop(a, n); i = 0; b = []; while i < n; b.push(i); b.push(i); b.pop; b.shift; i += 1; end; b.size; end
def t_unshift(a, n); i = 0; b = [1,2,3]; while i < n; b.unshift(i); b.shift; i += 1; end; b.size; end
def t_rotate(a, n); i = 0; b = [1,2,3,4,5,6,7,8]; while i < n; b.rotate!; i += 1; end; b[0]; end
def t_sortby(a, n); i = 0; b = [5,3,8,1,9,2,7,4]; while i < n; b.sort_by { |x| -x }; i += 1; end; end
def t_minby(a, n);  i = 0; b = [5,3,8,1,9,2,7,4]; while i < n; b.min_by { |x| -x }; i += 1; end; end
def t_max(a, n);    i = 0; b = [5,3,8,1,9,2,7,4]; while i < n; b.max; i += 1; end; end
def t_flatten(a, n);i = 0; b = [1,[2,[3,4]],5]; while i < n; b.flatten; i += 1; end; end
def t_slice(a, n);  i = 0; b = [1,2,3,4,5,6,7,8]; while i < n; b.each_slice(2) { |x| x }; i += 1; end; end
def t_ewo(a, n);    i = 0; b = [1,2,3,4]; while i < n; b.each_with_object([]) { |x, m| m << x }; i += 1; end; end
def t_zip(a, n);    i = 0; b = [1,2,3,4]; while i < n; b.zip(b); i += 1; end; end
def t_product(a, n);i = 0; b = [1,2,3]; while i < n; b.product(b); i += 1; end; end
def t_pack(a, n);   i = 0; b = [1,2,3,4]; while i < n; b.pack("C*"); i += 1; end; end
def t_sort(a, n);   i = 0; b = [5,3,8,1,9,2,7,4]; while i < n; b.sort; i += 1; end; end
def cpu; Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID); end
only = ARGV[0]
n = 200_000
%w[each any all map ewi inject sum pushpop unshift rotate sortby minby max flatten slice ewo zip product pack sort].each do |k|
  next if only && !only.split(",").include?(k)
  m = method("t_#{k}")
  a = [7]
  3.times { m.call(a, n) }
  best = (1..5).map { t = cpu; m.call(a, n); cpu - t }.min
  printf("%-8s %7.1f ns\n", k, best * 1e9 / n)
end
