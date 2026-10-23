# Predicate-mask fidelity regression

DXBC comparisons produce all-zero/all-one32-bit masks. The existing exporter
represents their results as float0/1. Numeric `~uint(predicate)` therefore cannot
implement predicate complement, and `uint(fraction) & uint(predicate)` destroys
fractional values. This repair only changes operations with proven mask provenance.

`USILAndOptimizer` tracks exact temporary-register lanes before/after each write,
including swizzled/overlapping copies, comparisons, movc of masks (or mask/zero),
and boolean AND/OR/XOR. All other writes invalidate their destination lanes.
Conditional joins intersect both paths. Loop-written lanes are invalidated at
entry/exit; a comparison inside the loop can still establish local provenance.
Only a proven NOT becomes the new LogicalNot instruction. AND with a proven mask
on either side becomes mask ? otherValue :0, preserving the other's float bits.
Unknown integer NOT and arbitrary nonpredicate bitwise operations are unchanged.
The former unconditional AND-with-float1 rewrite is removed because numerical1
is not the DXBC all-one bit mask.

Reapply narrowly with `python3 shaders/patch_predicate_masks.py`. It replaces only
the owned optimizer and inserts one enum member/emitter handler; texture code is
preserved. Run `dotnet run --project shaders/predicate-tests/PredicateTests.csproj
-c Release` from the workspace root. The test reads actual original FinalPass
DXBC programs24–47 and96–119, records their hashes and every optimization, and
checks predicate truth tables including fractional floats, negative zero andNaN.
These48 programs have304 NOT instructions; all must have proven mask operands.
The isolated GPU oracle has duplicate includes, hence its608 textual complements.

Limits: this is not a general untyped-register-to-HLSL bit-preservation rewrite.
Unknown numeric bitwise operations retain the prior emitter behavior; mixed vector
lanes or loop-carried types are never guessed. Native FinalPass pixel comparison
is owned by the critical shader probe and remains a separate end-to-end check.
