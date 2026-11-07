// Every file of a mod that compiles the core sees its types without a using.
// Pose is aliased because UnityEngine has a Pose too: an alias wins over the
// `using UnityEngine;` in each file, so the plain name means ours.
global using Dvr;
global using Pose = Dvr.Pose;
