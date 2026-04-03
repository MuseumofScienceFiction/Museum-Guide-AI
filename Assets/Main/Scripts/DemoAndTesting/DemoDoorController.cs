using System.Collections;
using UnityEngine;

namespace MoSF.Demos {
	public class DemoDoorController : MonoBehaviour {
		[Header("Door References")]
		public Transform leftDoor;
		public Transform rightDoor;

		[Header("Open Positions (Local)")]
		public Vector3 leftDoorOpenPos;
		public Vector3 rightDoorOpenPos;

		[Header("Settings")]
		public float movementSpeed = 5f;

		private Vector3 leftDoorClosedPos;
		private Vector3 rightDoorClosedPos;

		private Coroutine doorCoroutine;

		public bool openDoor;
		public bool closeDoor;

		private void Awake() {
			// Cache the starting local positions to use when closing the doors
			if (leftDoor != null) {
				leftDoorClosedPos = leftDoor.localPosition;
			}
			if (rightDoor != null) {
				rightDoorClosedPos = rightDoor.localPosition;
			}
		}

		// For editor testing
		private void Update() {
			if (openDoor) {
				openDoor = false;

				OpenDoor();
			}

			if (closeDoor) {
				closeDoor = false;

				CloseDoor();
			}
		}

		public void OpenDoor() {
			if (doorCoroutine != null) {
				StopCoroutine(doorCoroutine);
			}

			doorCoroutine = StartCoroutine(AnimateDoors(leftDoorOpenPos, rightDoorOpenPos));
		}

		public void CloseDoor() {
			if (doorCoroutine != null) {
				StopCoroutine(doorCoroutine);
			}

			doorCoroutine = StartCoroutine(AnimateDoors(leftDoorClosedPos, rightDoorClosedPos));
		}

		private IEnumerator AnimateDoors(Vector3 targetLeft, Vector3 targetRight) {
			if (leftDoor == null || rightDoor == null) {
				Debug.LogWarning("DemoDoorController: Door transforms are not assigned!");
				yield break;
			}

			// Loop until the doors are extremely close to their target positions
			while (Vector3.Distance(leftDoor.localPosition, targetLeft) > 0.001f ||
				   Vector3.Distance(rightDoor.localPosition, targetRight) > 0.001f) {
				
				leftDoor.localPosition = Vector3.Lerp(leftDoor.localPosition, targetLeft, Time.deltaTime * movementSpeed);
				rightDoor.localPosition = Vector3.Lerp(rightDoor.localPosition, targetRight, Time.deltaTime * movementSpeed);

				// Wait until the next frame
				yield return null;
			}

			// Snap to the exact final positions to ensure clean alignment
			leftDoor.localPosition = targetLeft;
			rightDoor.localPosition = targetRight;
		}
	}
}
