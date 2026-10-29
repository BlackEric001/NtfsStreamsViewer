PR bundle for GitHub
====================

STATE OF PLAY (verified in this sandbox):
- Your merged master still contains the BROKEN windows.rs
  (FILE_ATTRIBUTE_SEQUENTIAL_SCAN import, GetDriveTypeW from WindowsProgramming)
  -> that is why local build fails with E0432.
- The fix exists only as commits in this sandbox branch (never pushed:
  no remote/credentials here).

RECOMMENDED: option B
--------------
Option A - apply one clean patch onto your current master:
    git checkout -b fix/windows-v0.58 master
    git apply pr-bundle/0006-fix-windows-and-cleanup.patch   # or: git am < ...patch
    git push origin fix/windows-v0.58   # then open PR

Option B - fetch the full corrected branch from the bundle (no conflicts possible):
    git bundle unbundle pr-bundle/windows-fixes.bundle   # sanity check (optional)
    git checkout -b fix/windows-v0.58 master
    git merge --ff-only f0a56d6 2>/dev/null || true      # will not work across clones; use below instead
    # Correct way to import the bundle into YOUR clone:
    git remote add sandbox file:///path/to/this/workspace   # OR copy windows-fixes.bundle locally
    git fetch ./windows-fixes.bundle
    git checkout -b fix/windows-v0.58 FETCH_HEAD
    git push origin fix/windows-v0.58   # open PR against master

NOTE: commit 8dc19fa in history contains thousands of target/ build artifacts
(gitignore was broken at the time). They are harmless but bloat the repo;
the FINAL two commits (f0a56d6, 459941c) restore a proper .gitignore and
contain the real source state. If you want a pristine single-commit PR,
apply option A instead.
