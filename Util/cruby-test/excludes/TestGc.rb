exclude(:test_latest_gc_info_need_major_by, "loops until GC.latest_gc_info(:need_major_by) is set, which only CRuby's generational GC (RGenGC) does; it never ends on IronRuby")
