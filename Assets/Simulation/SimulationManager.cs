using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SimulationManager : MonoBehaviour
{
	[Header("Initial values")]
	public int dimension = 1024;
	public int numberOfParticles = 524280;
	public ComputeShader computeShader;
	public RenderTexture trail;

	[Header("Run time parameters")]
	public bool m_bActive = false;
	[Range(0f, 1.0f)] public float startRadius = 0.5f;
	[Range(0f, 1f)] public float deposit = 1.0f;
	[Range(0f, 1f)] public float decay = 0.002f;
	[Range(0f, 180f)] public float sensorAngleDegrees = 45f;  //in degrees
	[Range(0f, 180f)] public float rotationAngleDegrees = 45f;//in degrees
	[Range(0f, 0.1f)] public float sensorOffsetDistance = 0.01f;
	[Range(0f, 0.01f)] public float stepSize = 0.001f;

	[Header("Interaction")]
	public GameObject Pointer;
	[Range(0f, 1f)] public float pointerRadius = 0.01f;
	[Range(-1.0f, 1f)] public float pointerChemicalA = 0.0f;
	[Range(-1.0f, 1f)] public float pointerParticleAttraction = 0.0f;

	private float sensorAngle;              //in radians
	private float rotationAngle;            //in radians
	private Vector2 pointerUV;

	private int initHandle, trailHandle, particleHandle;
	private ComputeBuffer particleBuffer;

	private static int GroupCount = 8;       // Group size has to be same with the compute shader group size

	struct Particle
	{
		public Vector2 point;
		public float angle;

		public Particle(Vector2 pos, float angle)
		{
			point = pos;
			this.angle = angle;
		}
	};

	void OnValidate() // Called by Unity when someone changes a value in the Editor
	{
		if (dimension < GroupCount) dimension = GroupCount;
	}

	// Start is called before the first frame update
	void Start()
	{
		if (computeShader == null)
		{
			Debug.LogError("Simulation requires computerShader to work.");
			this.enabled = false;
			return;
		}

		// Compute shader connections...
		initHandle = computeShader.FindKernel("Init");
		particleHandle = computeShader.FindKernel("MoveParticles");
		trailHandle = computeShader.FindKernel("StepTrail");

		UpdateRuntimeParameters();
		InitializeParticles();
		InitializeTrail();
	}

	void InitializeParticles()
	{
		if (numberOfParticles > GroupCount * 65535) numberOfParticles = GroupCount * 65535;

		Debug.Log("Particles: " + numberOfParticles + "Thread groups: " + numberOfParticles / GroupCount);

		Particle[] data = new Particle[numberOfParticles];
		particleBuffer = new ComputeBuffer(data.Length, 12);
		particleBuffer.SetData(data);

		//initialize particles with random positions
		computeShader.SetInt("numberOfParticles", numberOfParticles);
		computeShader.SetVector("trailDimension", Vector2.one * dimension);
		computeShader.SetBuffer(initHandle, "particleBuffer", particleBuffer);

		Dispatch(initHandle, numberOfParticles / GroupCount, 1, 1);

		computeShader.SetBuffer(particleHandle, "particleBuffer", particleBuffer);
	}

	void InitializeTrail()
	{
		if (trail.enableRandomWrite == false)
		{
			trail.Release();
			trail.enableRandomWrite = true;
			trail.Create();
			Debug.Log("Recreate " + trail + " with enableRandomWrite = true");
		}
		Debug.Log(trail.format);

		// Set the TrailBuffer as the texture of the material of this objects
		var rend = GetComponent<Renderer>();
		rend.material.mainTexture = trail;

		computeShader.SetTexture(particleHandle, "TrailBuffer", trail);
		computeShader.SetTexture(trailHandle, "TrailBuffer", trail);
	}

	// Update is called once per frame
	void Update()
	{
		UpdatePointers();
		UpdateRuntimeParameters();

		if (m_bActive)
		{
			UpdateParticles();
			UpdateTrail();
		}
	}

/*	void OnDrawGizmos()
	{
		// Draw a yellow sphere at the transform's position
		Gizmos.color = Color.yellow;
		Gizmos.DrawSphere(Pointer.transform.position, 1);
	}
	*/
	void UpdatePointers()
	{
		if (Pointer == null)
			return;


		// We are going to shoot a ray out from our Pointer GameObject, in the Forward (+Z) direction 
		// and calculate where it intersects with our Simulation "Plane" object.

		RaycastHit hit;

		// Pointer is GameObject. Every GameObject has a member struct called "transform"
		Vector3 end = Pointer.transform.position + Pointer.transform.forward * 10;
		Debug.DrawLine(Pointer.transform.position, end, Color.green);

		if (!Physics.Raycast(Pointer.transform.position, Pointer.transform.forward, out hit))
		{
			// No intersection.. Lets change the color to green and return
			Pointer.GetComponent<Renderer>().material.color = Color.green;
			return;
		}
		// Note, we could also use a ray cast from camera through the mouse also... 
		//	if (!Physics.Raycast(cam.ScreenPointToRay(Input.mousePosition), out hit))

		// Hit!.  Change colour of Pointer to red and draw a red line to the hit point
		Pointer.GetComponent<Renderer>().material.color = Color.red;
		Debug.DrawLine(Pointer.transform.position, hit.point, Color.red);

		// Retrieve the UV coordinates of the hit point. These are the simulation-space coordinates.
		pointerUV = hit.textureCoord;
	}

	void UpdateRuntimeParameters()
	{
		computeShader.SetFloat("deltaTime", Time.deltaTime);
		sensorAngle = sensorAngleDegrees * 0.0174533f;
		rotationAngle = rotationAngleDegrees * 0.0174533f;
		computeShader.SetFloat("sensorAngle", sensorAngle);
		computeShader.SetFloat("rotationAngle", rotationAngle);
		computeShader.SetFloat("sensorOffsetDistance", sensorOffsetDistance);
		computeShader.SetFloat("stepSize", stepSize);
		computeShader.SetFloat("decay", decay);
		computeShader.SetFloat("deposit", deposit);
		computeShader.SetFloat("startRadius", startRadius);
		computeShader.SetVector("pointerUV", pointerUV);
		computeShader.SetFloat("pointerRadius", pointerRadius);
		computeShader.SetFloat("pointerChemicalA", pointerChemicalA);
		computeShader.SetFloat("pointerParticleAttraction", pointerParticleAttraction);
	}

	void UpdateParticles()
	{
		Dispatch(particleHandle, numberOfParticles / GroupCount, 1, 1);
	}

	void UpdateTrail()
	{
		Dispatch(trailHandle, dimension / GroupCount, dimension / GroupCount, 1);
	}

	void Dispatch(int kernelIndex, int threadGroupsX, int threadGroupsY, int threadGroupsZ)
	{
		computeShader.Dispatch(kernelIndex, threadGroupsX, threadGroupsY, threadGroupsZ);
	}

	void OnDestroy()
	{
		if (particleBuffer != null) particleBuffer.Release();
	}
}
// public class Physarum : SimulationManager