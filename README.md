## So, uh, what is this?
This forked repository contains Unity project files of a visualisation toolkit designed to create animated data visualisation transitions (also referred to as "morphs") in virtual and mixed reality.

The work is meant to be a follow up to our ACM CHI 2022 paper, titled [A Design Space For Data Visualisation Transformations Between 2D And 3D In Mixed-Reality Environments](https://dl.acm.org/doi/abs/10.1145/3491102.3501859).

The toolkit currently allows users to:
- Import morph specifications written in JSON using a Vega (and DxR) inspired grammar
- Define partial visualisation states that act as partial keyframes, dynamically applying animated transitions to eligible candidate visualisations at runtime
- Define signals that hook onto common events and parameters (e.g., hand inputs, head position) that allow for the triggering and control of transitions
- Define transition parameters to determine between what states can visualisations transition between and how they are triggered

New scripts and examples of the toolkit in use can be found in the Assets/VisMorphs folder. The Morphs.unity scene provides several examples of morphs that you can try out. The project uses MRTK and is currently targeted towards the Oculus Quest 2. The in-built simulator can be used to simulate hand input.

Numerous changes have also been made to the base DxR including:
- Maps using GeoJSON data
- Stacked and side-by-side barcharts
- Faceted visualisations (only tested with bar charts)
- Changing visualisation specifications via JSON during runtime via the Unity inspector
- Significant performance improvements (e.g., optimisation of specs parsing, reuse of marks and axes between visualisation updates)

Note that DxR interactions and GUI editor are not a priority of this work and therefore have not been tested at all. These are likely to be broken.

This project is still currently a work in progress and is intended to be submitted at an academic venue.

Development is lead by Benjamin Lee, and done in collaboration with Tim Dwyer and Bernie Jenny (Monash University), Arvind Satyanarayan (Massachusetts Institute of Technology), Maxime Cordeil (the University of Queensland), and Arnaud Prouzeau (Inria).

> ⚠️ Original DxR readme starts below ⚠️


## DXR: An Immersive Visualization Toolkit
DXR is a Unity package that makes it easy to create immersive data visualizations in XR (Augmented/Mixed/Virtual Reality). A visualization in DXR is a collection of Unity game objects whose visual properties (position, color, size, etc.) can be mapped to data attributes. The designer can specify this mapping interactively at runtime via a graphical user interface (GUI) or via a high-level programming interface, inspired by [Polestar](http://vega.github.io/polestar/) and [Vega-Lite](http://vega.github.io/vega-lite/), respectively. DXR is extensible, allowing the use of most Unity game objects for custom marks and channels. To learn more, check out the example and gallery previews below, as well as the DXR website:

### [DXR Website: https://sites.google.com/view/dxr-vis](https://sites.google.com/view/dxr-vis)

As a simple example, below, given a concise JSON specification (left) from the user, DXR generates an interactive visualization (right) in Unity.

<img src="docs/assets/img/example_template3D.png">

Below are some 2D and 3D visualization examples generated using DXR, which can be placed in AR/MR/VR applications.

<img src="docs/assets/img/gallery_overview.png">

DXR is work-in-progress and is currently under preparation for a research paper submission.  DXR is mainly developed by [Ronell Sicat](www.ronellsicat.com), and [Jiabao Li](https://www.jiabaoli.org/), with Hanspeter Pfister of the Visual Computing Group at Harvard University, in collaboration with Won-Ki Jeong, JunYoung Choi, Benjamin Bach, and Maxime Cordeil.
