# The child traps USR1 and then blocks in system("cat", :in => "fifo"), opening the fifo for the
# redirect itself. CRuby's open is interrupted by the signal (EINTR), runs the trap and goes back
# to the open; IronRuby runs a trap on the main thread when it reaches a safe point, and a
# thread inside open(2) never does, so the trap never runs, the parent waits for its output for
# ever, and the whole file hangs.
exclude(:test_execopts_redirect_open_fifo_interrupt_raise, "an open(2) blocked on a fifo does not run traps")
exclude(:test_execopts_redirect_open_fifo_interrupt_print, "an open(2) blocked on a fifo does not run traps")
