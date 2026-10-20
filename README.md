# Bridge Surface add-in for SOLIDWORKS — v0.0.1

An experimental SOLIDWORKS add-in that creates a **Bridge Surface** feature: one lightweight,
single/multi-span NURBS surface between two edges or sketch curves, with G0, G1 or G2 continuity on each side.

**Experimental, provided as is.** This is a curiosity release, shared free of charge, with no warranty
of any kind and no support commitment. Use it on copies of your work, not production files.

By AJ Design Studio LTD — [ajdesignstudio.co.nz](https://ajdesignstudio.co.nz)

## Before you use it

- **Parts that contain a Bridge Surface need this add-in.** On a PC without it, the feature cannot
  rebuild or be edited. SOLIDWORKS names the add-in as the provider of the missing feature.
  Check the result in SOLIDWORKS before sending such a part to anyone without the add-in.
- Tested on SOLIDWORKS 2025, Windows, parts only. Also ran on SOLIDWORKS 2021, on a PC that has 2025
  installed as well; other setups are untested.

## Requirements

- SOLIDWORKS 2025 (64-bit); 2021 also ran, see above
- .NET Framework 4.7.2

## Build and register

1. Open `BridgeSurfaceAddin.csproj` in Visual Studio **run as administrator**: the post-build step
   registers the DLL with 64-bit `regasm /codebase`.
2. Check `SwInstallDir` in the .csproj if SOLIDWORKS is not in the default folder.
3. Build `Release | x64`.
4. Start SOLIDWORKS, then Tools > Add-ins > tick **Bridge Surface**.

To uninstall: `regasm /u` on the DLL from an administrator prompt.

Or use the installer: compile `Installer/BridgeSurfaceAddin.iss` with Inno Setup after a Release build.
It installs the add-in and the three SOLIDWORKS interop DLLs, registers it, and unregisters it on uninstall.

## Use

Open a part, then **Bridge Surface** menu > Bridge Surface.

1. **Edges:** pick side A, then side B: a model edge, or a sketch line, arc or spline from a 2D or
   3D sketch. Optionally set a Start/End range (0–1, by arc length) per side to use part of it.
2. **Along edges (U):** Auto (follows the denser input), Manual (degree + CV count) or Fit
   (fewest CVs within the fit tolerance and the continuity tolerances).
3. **CV placement:** Averaged spaces the CVs evenly along each side's flow, overriding the magnitudes.
4. **Side A / Side B:** continuity (G0/G1/G2), flow direction, G1 and G2 magnitudes, and Flip.
5. Read the report at the top of the page, then OK. Right-click > Edit Feature reopens the page.

The report is measured on the surface that was built, using the same method as the free
Surface Analysis add-in's G2 test (benchmarked against Rhino's EdgeContinuity):

| Line | Meaning |
| --- | --- |
| G0 | largest distance between the surface boundary and the input, mm |
| G1 | largest angle between the bridge and face normals along the seam, degrees |
| G2 absolute | curvature difference (face − bridge) at the worst point, /mm |
| G2 relative | (R1 − R2)/(R1 + R2) at the same point, % |
| Surface | U and V degree and CV count of the result |

## Release notes

### v0.0.1 — first experimental release

- Bridge Surface macro feature: a real FeatureManager feature that rebuilds when its inputs change
  and supports Edit Feature.
- Single-span V direction: V degree set by continuity (G0/G0 = 1 up to G2/G2 = 5).
- G0, G1 and G2 per side, with G1 and G2 magnitudes and Flip.
- Flow options: Normal to edge, Match ends, Towards other edge, Isocurve, Free (Averaged only).
- Averaged CV placement: equal CV spacing along each side's flow.
- U parameterisation: Auto, Manual and Fit.
- Partial inputs: Start/End range per side, cut exactly from the input curve.
- Sketch curves as inputs (2D and 3D sketch lines, arcs and splines), G0 only.
- On an edge with two faces (split line, solid edge), Flip chooses which face to match.
- Measured G0/G1/G2 report, matching the Surface Analysis add-in's G2 test.
- Preview: CV net (side A magenta, side B red, G1 rows green, G2 rows orange) and zebra stripes on the
  bridge and adjacent faces, with mesh tolerance and chord angle controls.

## Known limitations

- One edge or one sketch segment per side: no edge chains, multi-segment curves, closed or periodic
  edges, or G3.
- Curve sides are G0 only (there is no face to match).
- Reference-curve inputs (Projected, Curve through points) are untested.
- Isocurve flow on a planar face follows the plane's own parameter axes, not the edge.
- Flip between two faces at a sharp angle (for example a thickened surface's edge and its end cap)
  can give unexpected row directions; not investigated.
- Fit adds CVs at a fixed degree; it does not raise the degree first.
- Bridges inserted by pre-release builds may fail to edit: delete and re-insert them.
- The output is always a separate sheet body; knitting is up to you.
- With the add-in installed but unticked in Tools > Add-ins, bridges still rebuild, but Edit Feature
  shows a SOLIDWORKS "serious error" message. Tick the add-in before editing a bridge.

## Licence

MIT, see [LICENSE](LICENSE).
