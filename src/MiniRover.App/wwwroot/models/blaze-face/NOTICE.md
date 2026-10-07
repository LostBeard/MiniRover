# BlazeFace (short range)

`model.tflite` is Google's MediaPipe BlazeFace short-range face detector, unmodified:
`blaze_face_short_range.tflite` (float16, version 1) from
https://storage.googleapis.com/mediapipe-models/face_detector/blaze_face_short_range/float16/1/blaze_face_short_range.tflite
(SHA-256 begins `b4578f35940bf5a1`).

- Licence: Apache License, Version 2.0 (https://www.apache.org/licenses/LICENSE-2.0), as stated in the model card:
  https://storage.googleapis.com/mediapipe-assets/MediaPipe%20BlazeFace%20Model%20Card%20(Short%20Range).pdf
- Created by Valentin Bazarevsky, Google. Citation: V. Bazarevsky et al., "BlazeFace: Sub-millisecond Neural Face
  Detection on Mobile GPUs", CVPR Workshop on Computer Vision for Augmented and Virtual Reality, 2019.

MiniRover uses it only to point the camera at a face (face tracking). It finds faces; it does not recognise who they
are. The model card lists surveillance and identity recognition as out of scope.
