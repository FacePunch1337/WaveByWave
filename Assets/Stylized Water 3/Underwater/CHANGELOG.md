3.2.7
Verified compatibility with Unity 6.4.

Added:
- Lowpass audio effect functionality for submerged cameras. See the "Audio" section of the documentation.

3.2.6

Changed:
- Supressed warning when a compressed reflection cubemap is used

Fixed:
- Pre-emptive fixes for obsolete code warnings/errors in Unity 6.4+

3.2.5

Changed:
- Underwater Area inspector now shows the currently active area. If selected the amount of submersion is displayed.
- Optimized the use of dozens of Underwater Area components by first checking intersection broadly

Fixed:
- Underwater Areas nested within another not appearing to be functional

3.2.4

Changed:
- Underwater surface transparency now factors in the deep color's alpha value.

Fixed:
- Waterline rendering not taking a custom water position offset into account
- Underwater shadows not working correctly for caustics if the Screen Space Shadows render feature was active

3.2.3

Fixed:
- Error regarding "SetRenderAttachmentDepth" when depth priming mode was Disabled in Unity 6.1

3.2.2

Fixed:
- Rendering taking effect even when camera is outside of an Underwater Area for components created before v3.2.0

Changed:
- Underwater Area is no longer rendering if the assigned Box Collider is disabled

3.2.1

Fixed:
- Some shading features (eg. Caustics/Translucency) being disabled in a build if not originally enabled in the editor (regression since v3.2.0)
- Memory leak when using Skybox lighting (0.9kb)

3.2.0
Requires v3.2.0 of the Stylized Water 3 base asset!

Added:
- "Render Method" option on the render feature. Specifies if rendering takes place before transparent materials, or during.
- Support for multiple simultaneous camera's (eg. split-screen)

Changed:
- Various improvements to underwater shading, surface clarity decreases as the camera goes deeper
- Improved camera submersion detection, now also factors in the screen corners.

3.1.1

Added:
- Underwater Area now supports rotated box colliders

Changed:
- Detection of the active Underwater Area now also factors which is closest, to better handle two or more overlapping areas

Fixed:
- Lake volume in demo not having the same material assigned as the water mesh
- Removed missing prefabs from demo scene (remnant of showcasing materials)
- Waterline Lens Offset parameter also appearing to clip the underwater fog near the camera

3.1.0

Added:
- Particle Effect controller, makes effects follow the camera underwater up to a specific depth (eg. sunshafts)
- Support for mobile hardware.

Changed:
- Rewritten rendering for Unity's Render Graph
- Optimized technical design, no longer uses full-screen post-processing.
- Redesigned to work exclusively with defined underwater areas (box collider triggers)
- Revised shader for transparency, now only blend the alpha value.

Removed:
- Blur/distortion effects, incurred maintenance overhead as URP's rendering code kept changing. Considered niche as AAA games do not use distortion effects underwater.
- Volume-based settings blending functionality (deemed unused).