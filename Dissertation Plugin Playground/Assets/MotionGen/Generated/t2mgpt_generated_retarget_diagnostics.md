# MotionGen Retarget Diagnostics

- Animator: Banana Man
- Avatar: Banana ManAvatar
- Frames: 80
- Source FPS: 20
- Calibration enabled: yes

## Root contract
- Avg dot(root forward, body forward): -0.008
- Avg dot(inverse root forward, body forward): -0.009
- Avg dot(root forward, travel dir): 0.698
- Avg dot(inverse root forward, travel dir): 0.702
- Recommendation: Root contract is not obviously inverted from this sample. Bone-basis mismatch is likely dominating.

## Source skeleton fidelity
- Mean joint position reconstruction error: 0.00819
- Max joint position reconstruction error: 0.08935
- Mean FK-carrier reconstruction error: 0.00819
- Max FK-carrier reconstruction error: 0.08935
- Mean angle between `rootRotation` and `localRotations[0]`: 0.000°
- Max angle between `rootRotation` and `localRotations[0]`: 0.000°
- Recommendation: Source motion is mostly self-consistent, with some contract drift. Humanoid interpretation is still the prime suspect.

| Bone | Mean error | Max error |
|---|---:|---:|
| pelvis | 0.00000 | 0.00000 |
| left_hip | 0.00000 | 0.00000 |
| right_hip | 0.00000 | 0.00000 |
| spine1 | 0.00000 | 0.00000 |
| left_knee | 0.00000 | 0.00000 |
| right_knee | 0.00000 | 0.00000 |
| spine2 | 0.00000 | 0.00000 |
| left_ankle | 0.00000 | 0.00000 |
| right_ankle | 0.00000 | 0.00000 |
| spine3 | 0.00000 | 0.00000 |
| left_foot | 0.00000 | 0.00000 |
| right_foot | 0.00000 | 0.00000 |
| neck | 0.00000 | 0.00000 |
| left_collar | 0.01015 | 0.02084 |
| right_collar | 0.01642 | 0.03033 |
| head | 0.00000 | 0.00000 |
| left_shoulder | 0.00890 | 0.01987 |
| right_shoulder | 0.02031 | 0.03649 |
| left_elbow | 0.02584 | 0.04804 |
| right_elbow | 0.01540 | 0.03749 |
| left_wrist | 0.04948 | 0.08935 |
| right_wrist | 0.03365 | 0.07313 |

### Quaternion conversion candidates
| Candidate | Mean error | Max error |
|---|---:|---:|
| Current sign-flip | 0.00819 | 0.08935 |
| Mirrored basis via LookRotation | 1.45011 | 3.31175 |
| Current sign-flip + inverse | 1.13088 | 2.57263 |
| Mirrored basis + inverse | 1.47269 | 3.58612 |

If one candidate is dramatically better, the main problem is the source quaternion handedness/basis conversion, not Humanoid retargeting.

## Source humanoid abstraction
- Mean per-frame muscle delta: 0.01688
- Max single-muscle frame delta: 0.43759
- Mean per-frame bodyPosition delta: 0.38550
- Mean per-frame bodyRotation delta: 1.54069°
- Interpretation: Unity Humanoid is registering meaningful motion from the source rig; the remaining issue is likely target retarget semantics or source avatar definition details.

## Source humanoid roundtrip loss
- Mean source→humanoid→source joint error: 0.11071
- Max source→humanoid→source joint error: 0.31671
- Mean exact-rig per-frame joint delta: 0.19819
- Mean roundtripped per-frame joint delta: 0.19749
- Roundtripped/exact joint delta ratio: 0.996
- Interpretation: the source Humanoid roundtrip keeps some motion amplitude, but it is materially changing the actual source pose. The remaining issue is still upstream of target retargeting and clip baking.

## Target retarget transfer
- Mean source per-frame muscle delta: 0.01688
- Mean target per-frame muscle delta: 0.01828
- Target/source muscle delta ratio: 1.083
- Mean source→target muscle error: 0.01307
- Max source→target single-muscle error: 0.93745
- Mean source→target bodyPosition error: 0.00011
- Mean source→target bodyRotation error: 0.00000°
- Interpretation: target retarget transfer is preserving most of the source human-pose motion. If the visible clip still looks wrong, investigate clip baking/application rather than source-to-target humanoid transfer.

## Muscle range transfer
| Muscle | Source range | Target range | Ratio |
|---|---:|---:|---:|
| Left Foot Up-Down | 1.30016 | 1.08254 | 0.833 |
| Right Foot Up-Down | 1.36174 | 1.14470 | 0.841 |
| Right Arm Twist In-Out | 0.95017 | 0.80369 | 0.846 |
| Left Arm Twist In-Out | 0.93438 | 0.82387 | 0.882 |
| Left Forearm Twist In-Out | 0.66427 | 0.62626 | 0.943 |
| Left Hand In-Out | 0.22409 | 0.21235 | 0.948 |
| Right Hand In-Out | 0.30835 | 0.29697 | 0.963 |
| Left Arm Down-Up | 0.14797 | 0.14719 | 0.995 |
| Right Lower Leg Stretch | 0.47032 | 0.46969 | 0.999 |
| Right Upper Leg In-Out | 0.29497 | 0.29480 | 0.999 |
| Neck Tilt Left-Right | 0.05209 | 0.05209 | 1.000 |
| Spine Twist Left-Right | 0.10719 | 0.10718 | 1.000 |
| Neck Turn Left-Right | 0.04787 | 0.04787 | 1.000 |
| Head Nod Down-Up | 0.21964 | 0.21964 | 1.000 |
| Right Shoulder Down-Up | 0.41017 | 0.41017 | 1.000 |
| Chest Twist Left-Right | 0.04699 | 0.04699 | 1.000 |
| UpperChest Front-Back | 0.45529 | 0.45529 | 1.000 |
| Left Toes Up-Down | 0.63758 | 0.63758 | 1.000 |

- Interpretation: no large muscle-range collapse is visible among the dominant muscles. The remaining issue is more likely directional semantics than raw muscle amplitude.

## Target world-space motion transfer
- Mean source tracked-bone per-frame delta: 0.09223
- Mean direct-target tracked-bone per-frame delta: 0.40351
- Mean baked-target tracked-bone per-frame delta: 0.40335
- Direct/source tracked-bone delta ratio: 4.375
- Baked/direct tracked-bone delta ratio: 1.000

| Bone | Source excursion | Direct target excursion | Baked target excursion | Direct/source | Baked/direct |
|---|---:|---:|---:|---:|---:|
| Hips | 0.64498 | 2.99228 | 2.99228 | 4.639 | 1.000 |
| LeftFoot | 0.62369 | 2.83027 | 2.83027 | 4.538 | 1.000 |
| RightFoot | 0.77193 | 2.75316 | 2.75318 | 3.567 | 1.000 |
| LeftToes | 0.60359 | 2.82979 | 2.83020 | 4.688 | 1.000 |
| RightToes | 0.80093 | 2.76259 | 2.76828 | 3.449 | 1.002 |
| Head | 0.63651 | 2.98932 | 2.98932 | 4.696 | 1.000 |
| LeftHand | 0.63433 | 3.14089 | 3.14101 | 4.952 | 1.000 |
| RightHand | 0.64693 | 2.87666 | 2.87647 | 4.447 | 1.000 |

- Interpretation: tracked target-bone world motion is broadly preserved through direct retarget and baking. If the result still looks wrong, inspect specific bone trajectories or scene playback conditions rather than overall motion amplitude.

## Target relative-pose motion
- Mean source relative-pose per-frame delta: 0.03078
- Mean direct-target relative-pose per-frame delta: 0.02909
- Mean baked-target relative-pose per-frame delta: 0.02901
- Direct/source relative-pose delta ratio: 0.945
- Baked/direct relative-pose delta ratio: 0.997

| Bone | Source relative excursion | Direct target relative excursion | Baked target relative excursion | Direct/source | Baked/direct |
|---|---:|---:|---:|---:|---:|
| LeftFoot | 0.37087 | 0.26046 | 0.26047 | 0.702 | 1.000 |
| RightFoot | 0.40025 | 0.40598 | 0.40596 | 1.014 | 1.000 |
| LeftToes | 0.38777 | 0.26901 | 0.26501 | 0.694 | 0.985 |
| RightToes | 0.42721 | 0.38674 | 0.38631 | 0.905 | 0.999 |
| Head | 0.06261 | 0.02689 | 0.02689 | 0.429 | 1.000 |
| LeftHand | 0.12951 | 0.22483 | 0.22490 | 1.736 | 1.000 |
| RightHand | 0.15685 | 0.20071 | 0.20081 | 1.280 | 1.000 |

- Interpretation: root-relative pose motion is broadly preserved. If the pose still looks static, inspect specific bone semantics or avatar muscle mapping rather than overall articulated amplitude.

## Gait semantic transfer
| Metric | Source | Target | Ratio |
|---|---:|---:|---:|
| Left foot forward range | 1.57466 | 0.01336 | 0.008 |
| Right foot forward range | 1.41124 | 0.00945 | 0.007 |
| Left foot height range | 0.32406 | 0.01211 | 0.037 |
| Right foot height range | 0.30785 | 0.01889 | 0.061 |
| Left foot lateral range | 1.54182 | 0.01114 | 0.007 |
| Right foot lateral range | 1.36469 | 0.01076 | 0.008 |
| Left hand forward range | 0.59458 | 0.00921 | 0.015 |
| Right hand forward range | 0.59949 | 0.00985 | 0.016 |
| Head vertical range | 0.01764 | 0.00043 | 0.025 |
| Left knee bend range | 60.11216 | 37.32187 | 0.621 |
| Right knee bend range | 60.58317 | 36.98628 | 0.611 |
| Left elbow bend range | 33.23744 | 6.53107 | 0.196 |
| Right elbow bend range | 31.67183 | 6.28691 | 0.199 |

- Interpretation: overall gait ranges are broadly preserved. The remaining issue is likely specific directional semantics or avatar muscle mapping, not gross amplitude loss.

## Fixed-frame gait semantic transfer
| Metric | Source | Target | Ratio |
|---|---:|---:|---:|
| Left foot forward range | 0.22463 | 0.29717 | 1.323 |
| Right foot forward range | 0.42653 | 0.25006 | 0.586 |
| Left foot height range | 0.38863 | 0.24809 | 0.638 |
| Right foot height range | 0.36926 | 0.35255 | 0.955 |
| Left foot lateral range | 1.13885 | 0.45959 | 0.404 |
| Right foot lateral range | 1.07057 | 0.40647 | 0.380 |
| Left hand forward range | 0.21010 | 0.16741 | 0.797 |
| Right hand forward range | 0.19945 | 0.17344 | 0.870 |
| Head vertical range | 0.01744 | 0.01210 | 0.694 |

- Interpretation: much of the earlier collapse came from the rotating-frame measurement. The remaining issue is more likely directional mapping than total end-effector loss.

## End-effector direction transfer
| Effector | Src Lat% | Src Up% | Src Fwd% | Tgt Lat% | Tgt Up% | Tgt Fwd% | Mean dir cosine |
|---|---:|---:|---:|---:|---:|---:|---:|
| LeftFoot | 61.7 | 24.5 | 13.8 | 38.0 | 28.0 | 34.0 | 0.479 |
| RightFoot | 60.1 | 22.6 | 17.4 | 28.2 | 43.3 | 28.6 | -0.261 |
| LeftHand | 61.0 | 13.8 | 25.2 | 41.1 | 35.3 | 23.7 | -0.230 |
| RightHand | 62.3 | 17.3 | 20.4 | 43.5 | 30.0 | 26.6 | 0.563 |

- Interpretation: foot swing directions are poorly aligned between source and target even though amplitude survives. The remaining issue is directional mapping, not motion loss.

## Root locomotion contract
- Source root cumulative travel: 15.40106
- Target bodyPosition cumulative travel: 30.45407
- Target hips cumulative travel: 31.71982
- Target animator transform cumulative travel: 0.00000
- Source root net displacement: 1.03531
- Target bodyPosition net displacement: 2.05876
- Target hips net displacement: 2.15564
- Target animator transform net displacement: 0.00000
- bodyPosition/source cumulative ratio: 1.977
- hips/source cumulative ratio: 2.060
- animator/source cumulative ratio: 0.000
- Interpretation: locomotion is staying inside the humanoid body/hips and is not transferring to the animator transform. The remaining issue is root-motion contract, not pose amplitude.

## Baked clip roundtrip
- Mean baked muscle error: 0.01043
- Max baked single-muscle error: 0.37519
- Mean baked bodyPosition error: 0.00009
- Mean baked bodyRotation error: 0.00000°
- Baked/source frame-delta ratio: 1.039
- Interpretation: the baked clip roundtrip is faithful. If the visible result is still wrong, investigate how the clip is being applied or previewed outside the diagnostic path.

## Bone basis mismatch
- Target neutral capture: captured from neutral humanoid muscles using mapped bone world rotations

| Bone | Source→target basis delta | Source aim | Target aim | Source pole | Target pole | Stored calibration | Notes |
|---|---:|---|---|---|---|---:|---|
| Hips | 25.5° | (0.00, 1.00, 0.00) | (0.11, 0.93, 0.35) | (0.00, 0.00, 1.00) | (0.21, -0.37, 0.91) | 0.0° | small mismatch |
| LeftUpperLeg | 76.1° | (0.00, -1.00, 0.00) | (-0.12, -0.98, 0.17) | (0.00, 0.00, -1.00) | (0.96, -0.16, -0.24) | 0.0° | strong mismatch |
| RightUpperLeg | 74.8° | (0.00, -1.00, 0.00) | (0.13, -0.99, 0.11) | (0.00, 0.00, -1.00) | (0.96, 0.09, -0.28) | 0.0° | moderate mismatch |
| Spine | 18.9° | (0.00, 1.00, 0.00) | (0.08, 0.97, 0.21) | (0.00, 0.00, 1.00) | (0.23, -0.22, 0.95) | 0.0° | small mismatch |
| LeftLowerLeg | 96.4° | (0.00, -1.00, 0.00) | (-0.35, -0.35, -0.87) | (0.00, 0.00, -1.00) | (0.93, -0.06, -0.35) | 0.0° | strong mismatch |
| RightLowerLeg | 102.5° | (0.00, -1.00, 0.00) | (-0.12, -0.36, -0.93) | (0.00, 0.00, -1.00) | (0.99, 0.02, -0.13) | 0.0° | strong mismatch |
| Chest | 18.2° | (0.00, 1.00, 0.00) | (0.08, 0.98, 0.19) | (0.00, 0.00, 1.00) | (0.23, -0.21, 0.95) | 0.0° | small mismatch |
| LeftFoot | 94.7° | (0.00, 0.00, -1.00) | (-0.07, -0.99, 0.08) | (1.00, 0.00, 0.00) | (0.97, -0.09, -0.22) | 0.0° | strong mismatch |
| RightFoot | 93.5° | (0.00, 0.00, -1.00) | (0.04, -1.00, 0.05) | (1.00, 0.00, 0.00) | (0.98, 0.03, -0.19) | 0.0° | strong mismatch |
| UpperChest | 15.6° | (0.00, 1.00, 0.00) | (0.06, 0.99, 0.11) | (0.00, 0.00, 1.00) | (0.23, -0.12, 0.96) | 0.0° | small mismatch |
| Neck | 137.3° | (0.00, 0.00, -1.00) | (0.19, 0.73, 0.66) | (1.00, 0.00, 0.00) | (0.16, -0.68, 0.72) | 0.0° | strong mismatch |
| LeftShoulder | 128.3° | (0.00, -1.00, 0.00) | (-1.00, -0.03, 0.10) | (0.00, 0.00, -1.00) | (-0.01, 0.99, 0.17) | 0.0° | strong mismatch |
| RightShoulder | 168.9° | (0.00, -1.00, 0.00) | (0.92, -0.09, -0.38) | (0.00, 0.00, -1.00) | (0.37, -0.13, 0.92) | 0.0° | strong mismatch |
| LeftUpperArm | 82.8° | (0.00, -1.00, 0.00) | (-0.59, -0.71, 0.38) | (0.00, 0.00, -1.00) | (0.72, -0.68, -0.16) | 0.0° | strong mismatch |
| RightUpperArm | 174.6° | (0.00, -1.00, 0.00) | (0.65, -0.75, 0.07) | (0.00, 0.00, -1.00) | (-0.10, 0.01, 0.99) | 0.0° | strong mismatch |
| LeftLowerArm | 121.4° | (0.00, -1.00, 0.00) | (0.59, -0.18, 0.78) | (0.00, 0.00, -1.00) | (0.61, 0.73, -0.29) | 0.0° | strong mismatch |
| RightLowerArm | 144.8° | (0.00, -1.00, 0.00) | (-0.17, -0.16, 0.97) | (0.00, 0.00, -1.00) | (-0.61, 0.79, 0.02) | 0.0° | strong mismatch |

### Neutral-frame local axis comparison

| Bone | Src local aim | Src local pole | Tgt local aim | Tgt local pole | Local correction | Notes |
|---|---|---|---|---|---:|---|
| Hips | (0.00, 1.00, 0.00) | (0.00, 0.00, 1.00) | (0.11, 0.93, 0.35) | (0.21, -0.37, 0.91) | 25.5° | small local offset |
| LeftUpperLeg | (0.00, -1.00, 0.00) | (0.00, 0.00, -1.00) | (-0.13, -0.86, 0.49) | (0.99, -0.13, 0.04) | 94.5° | strong static offset candidate |
| RightUpperLeg | (0.00, -1.00, 0.00) | (0.00, 0.00, -1.00) | (0.12, -0.86, 0.49) | (0.99, 0.09, -0.08) | 90.9° | strong static offset candidate |
| Spine | (0.00, 1.00, 0.00) | (0.00, 0.00, 1.00) | (0.01, 0.99, -0.15) | (0.00, 0.15, 0.99) | 8.7° | small local offset |
| LeftLowerLeg | (0.00, 1.00, 0.00) | (0.00, 0.00, 1.00) | (-0.97, 0.24, -0.07) | (-0.10, -0.11, 0.99) | 76.6° | strong static offset candidate |
| RightLowerLeg | (0.00, 1.00, 0.00) | (0.00, 0.00, 1.00) | (-0.96, 0.24, 0.11) | (0.13, 0.09, 0.99) | 76.7° | strong static offset candidate |
| Chest | (0.00, 1.00, 0.00) | (0.00, 0.00, 1.00) | (0.00, 1.00, -0.02) | (0.00, 0.02, 1.00) | 1.0° | small local offset |
| LeftFoot | (0.00, 0.00, 1.00) | (1.00, 0.00, 0.00) | (0.95, 0.31, -0.04) | (0.08, -0.12, 0.99) | 162.1° | very strong static offset candidate |
| RightFoot | (0.00, 0.00, 1.00) | (1.00, 0.00, 0.00) | (0.95, 0.31, 0.01) | (-0.03, 0.05, 1.00) | 169.3° | very strong static offset candidate |
| UpperChest | (0.00, 1.00, 0.00) | (0.00, 0.00, 1.00) | (0.00, 1.00, -0.08) | (0.00, 0.08, 1.00) | 4.9° | small local offset |
| Neck | (0.00, 0.00, -1.00) | (1.00, 0.00, 0.00) | (0.00, 0.81, 0.59) | (0.00, -0.59, 0.81) | 142.4° | very strong static offset candidate |
| LeftShoulder | (0.00, -1.00, 0.00) | (0.00, 0.00, -1.00) | (-0.99, -0.07, -0.14) | (-0.08, 1.00, 0.04) | 114.4° | strong static offset candidate |
| RightShoulder | (0.00, -1.00, 0.00) | (0.00, 0.00, -1.00) | (0.99, -0.07, -0.14) | (0.14, 0.00, 0.99) | 174.1° | very strong static offset candidate |
| LeftUpperArm | (0.00, 1.00, 0.00) | (0.00, 0.00, 1.00) | (-0.43, 0.64, -0.64) | (-0.02, -0.71, -0.70) | 169.0° | very strong static offset candidate |
| RightUpperArm | (0.00, 1.00, 0.00) | (0.00, 0.00, 1.00) | (0.65, 0.64, 0.40) | (-0.08, -0.47, 0.88) | 50.7° | moderate static offset candidate |
| LeftLowerArm | (0.00, 1.00, 0.00) | (0.00, 0.00, 1.00) | (0.90, 0.08, 0.42) | (0.09, -1.00, 0.00) | 104.5° | strong static offset candidate |
| RightLowerArm | (0.00, 1.00, 0.00) | (0.00, 0.00, 1.00) | (0.16, 0.08, 0.98) | (-0.06, -0.99, 0.09) | 85.4° | strong static offset candidate |

Interpretation notes:
- Large shoulder / upper-arm deltas indicate the canonical source basis is not a Unity-humanoid T-pose basis.
- Large foot deltas indicate ankle/foot local-axis disagreement, which shows up as toes pitching upward.
- Large hip / upper-leg deltas indicate abduction/twist mismatch, which shows up as legs splaying outward.
- Large neutral local correction angles indicate a strong candidate for a per-bone static anatomical offset in the retarget path.

