#!/usr/bin/env perl
# Renames the MapRenderer.Jobs. namespace to MapRenderer.Unity.Jobs. wherever it appears in a
# dot-terminated form (namespace declarations, using directives, fully-qualified type references,
# <see cref> doc comments). Self-locating: resolves the repo root via `git rev-parse
# --show-toplevel` and reads the tracked file list from there, so it runs correctly from any cwd.
#
# Scope: every git-tracked *.cs file, plus Tools/core-tests/core-tests.csproj (its <Compile
# Include> comments name the Jobs source tree). Markdown docs are OUT OF SCOPE — a doc's
# "MapRenderer.Jobs" mention needs a human judgement call (current-state wording vs kept dated
# history), not a mechanical substitution.
#
# A BARE "MapRenderer.Jobs" mention (no trailing dot — prose naming the folder/assembly) is not
# this script's job either: it needs a human to pick the right current-state wording, so the
# script only REPORTS where those remain (see "Bare-form residue" below).
#
# Idempotent by construction: after a run, the substring "MapRenderer.Jobs." (with the dot) no
# longer appears anywhere in the swept file set, so a second run changes zero files. The
# "Dot-form residue" count below is that self-check — it must always read 0 after a run, or the
# substitution has a bug.
#
# Usage:
#   Tools/rename-jobs-namespace.pl            # rewrite in place, report residue
#   Tools/rename-jobs-namespace.pl --check    # report what WOULD change, without writing

use strict;
use warnings;

my $check = (grep { $_ eq '--check' } @ARGV) ? 1 : 0;
my $self  = 'Tools/rename-jobs-namespace.pl';

chomp(my $repoRoot = `git rev-parse --show-toplevel 2>/dev/null`);
die "rename-jobs-namespace.pl: not inside a git repository\n" if !$repoRoot || $? != 0;
chdir $repoRoot or die "rename-jobs-namespace.pl: cannot chdir to $repoRoot: $!\n";

my @files;
{
    open(my $fh, '-|', 'git', 'ls-files', '-z', '--', '*.cs')
        or die "rename-jobs-namespace.pl: git ls-files failed: $!\n";
    local $/ = "\0";
    while (my $line = <$fh>) { chomp $line; push @files, $line if length $line; }
    close $fh;
}
push @files, 'Tools/core-tests/core-tests.csproj';
@files = grep { $_ ne $self } @files;

my $filesChanged = 0;
my $totalSubs    = 0;

for my $file (@files) {
    open(my $in, '<', $file) or die "rename-jobs-namespace.pl: cannot read $file: $!\n";
    local $/;
    my $text = <$in>;
    close $in;

    my $subs = () = $text =~ /\bMapRenderer\.Jobs\./g;
    next if $subs == 0;

    (my $rewritten = $text) =~ s/\bMapRenderer\.Jobs\./MapRenderer.Unity.Jobs./g;

    $totalSubs += $subs;
    $filesChanged++;
    printf "%s  %s (%d substitution%s)\n", $check ? '[check]' : '[rename]', $file, $subs, $subs == 1 ? '' : 's';

    next if $check;

    open(my $out, '>', $file) or die "rename-jobs-namespace.pl: cannot write $file: $!\n";
    print $out $rewritten;
    close $out;
}

print "\n$filesChanged file(s), $totalSubs substitution(s)", $check ? " (--check: nothing written)\n" : "\n";

exit 0 if $check;

# ── Residue report ──────────────────────────────────────────────────────────────────────────────────
# Dot-form: must be 0 after a real run — a non-zero count means this script's own substitution is broken.
my $dotResidue = 0;
my @bareResidue;
for my $file (@files) {
    open(my $in, '<', $file) or die "rename-jobs-namespace.pl: cannot re-read $file: $!\n";
    local $/;
    my $text = <$in>;
    close $in;

    $dotResidue += () = $text =~ /\bMapRenderer\.Jobs\./g;
    while ($text =~ /\bMapRenderer\.Jobs\b(?!\.)/g) {
        my $before = substr($text, 0, $-[0]);
        my $lineNo = 1 + (() = $before =~ /\n/g);
        push @bareResidue, "$file:$lineNo";
    }
}

print "\nDot-form residue (must be 0): $dotResidue\n";
if (@bareResidue) {
    print "Bare-form residue (MapRenderer.Jobs with no trailing dot — needs a human wording call, not this script):\n";
    print "  $_\n" for @bareResidue;
} else {
    print "Bare-form residue: 0\n";
}

exit($dotResidue > 0 ? 1 : 0);
