## Submission #4579 - Deimos: A Grammar of Dynamic Embodied Immersive Visualisation Morphs and Transitions
Deimos is a toolkit designed to assist in the creation of Dynamic, Embodied, and Immersive MOrphS. Morphs are collections of states, signals, and transition specifications that define how animated transition(s) should function and behave in an immersive environment. It is built on top of [DxR](https://github.com/ronellsicat/DxR) (created by Ronell Sicat et al.).

### Functions
Deimos, via its grammar, allows users to:
- Write and import Morph specifications written in JSON using a Vega (and DxR) inspired grammar
- Define partial visualisation states that act as partial keyframes, dynamically applying animated transitions to eligible candidate visualisations at runtime
- Define signals that hook onto common events and parameters (e.g., hand inputs, head position) that allow for the triggering and control of transitions
- Define transition parameters to determine between what states can visualisations transition between and how they are triggered

Note that Deimos is not intended to be a production ready toolkit.

### Usage
A Unity version of at least 2021.3.6f1 is required.

The example scene can be found in the *Assets/Deimos/Examples* folder. The subfolders contain all JSON specifications for the Morphs and DxR visualisations used in the example scene.

⚠️ For the purposes of anonymity, the URL to the Deimos JSON schema has been removed from all examples and the starting morph specification (when using the Asset Create window), as these were originally using a GitHub reference. You will need to manually add the schema reference using either a relative or absolute path by adding this line as the first property of the specification: "$schema": [URL GOES HERE] ⚠️

Deimos is built around MRTK 2 and has been tested using a tethered Oculus Quest 2 (with both controller and hand tracking input). The MRTK simulator can also be used to simulate hand input. **Deimos has not been tested for use in standalone VR/MR applications**.

A walkthrough and FAQ for how to use Deimos can be found in the supplementary zip file.

### Updates to DxR
Several features have been added to DxR during the development of Deimos. These include:
- Significant performance improvements (e.g., optimisation of specs and data parsing, reuse of marks and axes between visualisation updates)
- Changing visualisation specifications via JSON during runtime via the Unity inspector (re-implemented from DxR)
- Maps using GeoJSON data
- Stacked and side-by-side barcharts via offsets
- Faceted visualisations with options for curved and spherical layouts

⚠️ Note that DxR's interactions and GUI editor are not a priority of this work and therefore have not been tested or updated at all. These are likely to be broken. ⚠️