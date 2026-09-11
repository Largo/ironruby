#!/bin/bash
# run one spec file, show full output
cd /root/workspace/ironruby/.claude/worktrees/agent-ac29be6f3dc7db969
exec timeout 90 env RUBY_EXE=./ir.sh ./ir.sh -Imspec/lib mspec/bin/mspec-run "$@"
