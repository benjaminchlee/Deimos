## Deimos: A Grammar and Toolkit for Animated Transitions and Morphs in Immersive Analytics Environments
Deimos is a toolkit designed to assist in the creation of Dynamic, Embodied, and Immersive MOrphS. Morphs are collections of states, signals, and transition specifications that define how animated transition(s) should function and behave in an immersive environment. It is built on top of [DxR](https://github.com/ronellsicat/DxR) (created by Ronell Sicat et al.).

The work is meant to be a follow up to our ACM CHI 2022 paper, titled [A Design Space For Data Visualisation Transformations Between 2D And 3D In Mixed-Reality Environments](https://dl.acm.org/doi/abs/10.1145/3491102.3501859), and is currently tageted for submission at an academic venue.

### Functions
Deimos, via its grammar, allows users to:
- Write and import Morph specifications written in JSON using a Vega (and DxR) inspired grammar
- Define partial visualisation states that act as partial keyframes, dynamically applying animated transitions to eligible candidate visualisations at runtime
- Define signals that hook onto common events and parameters (e.g., hand inputs, head position) that allow for the triggering and control of transitions
- Define transition parameters to determine between what states can visualisations transition between and how they are triggered

Note that Deimos is not intended to be a production ready toolkit.

### Usage
The example scene can be found in the *Assets/Deimos/Examples* folder. The subfolders contain all JSON specifications for the Morphs and DxR visualisations used in the example scene.

Deimos is built around MRTK 2 and has been tested using a tethered Oculus Quest 2 (with both controller and hand tracking input). The MRTK simulator can also be used to simulate hand input. **Deimos has not been tested for use in standalone VR/MR applications**.

For a walkthrough and FAQ for how to use Deimos, please see ⚠️ TODO: ADD URL HERE ⚠️

### Updates to DxR
Several features have been added to DxR during the development of Deimos. These include:
- Significant performance improvements (e.g., optimisation of specs and data parsing, reuse of marks and axes between visualisation updates)
- Changing visualisation specifications via JSON during runtime via the Unity inspector (re-implemented from DxR)
- Maps using GeoJSON data
- Stacked and side-by-side barcharts via offsets
- Faceted visualisations with options for curved and spherical layouts

⚠️ Note that DxR's interactions and GUI editor are not a priority of this work and therefore have not been tested or updated at all. These are likely to be broken. ⚠️

### Acknowledgements
Development is lead by Benjamin Lee, and done in collaboration with Tim Dwyer and Bernie Jenny (Monash University), Arvind Satyanarayan (Massachusetts Institute of Technology), Maxime Cordeil (the University of Queensland), and Arnaud Prouzeau (Inria).

Deimos uses several third party toolkits and projects:
- [DxR](https://github.com/ronellsicat/DxR)
- [Mixed Reality Toolkit](https://github.com/microsoft/MixedRealityToolkit-Unity)
- [UniRx](https://github.com/neuecc/UniRx)
- [DynamicExpresso](https://github.com/dynamicexpresso/DynamicExpresso)
- [JSON.NET](https://www.newtonsoft.com/json)
