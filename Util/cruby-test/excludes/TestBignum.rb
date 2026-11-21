# It cubes two Bignums each round until a signal handler runs in the middle of their division.
# CRuby's bigdivrem checks for interrupts; .NET's BigInteger division cannot be interrupted, so
# the numbers grow without end and the file never finishes.
exclude(:test_interrupt_during_bigdivrem, "BigInteger division cannot be interrupted")
