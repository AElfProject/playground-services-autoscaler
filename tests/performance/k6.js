import http from "k6/http";
import { check } from "k6";

export const options = {
  vus: 1, // Number of virtual users
  duration: "3m", // Duration of the test
};

const binFile = open("./Archive.zip", "b");

export default function () {
  const url = "https://redis-job-runner.test.aelf.dev/test"; // Replace with your endpoint

  // Create the payload for multipart/form-data request
  const payload = {
    file: http.file(binFile, "Archive.zip"),
  };

  // Send the POST request with the multipart/form-data payload
  const res = http.post(url, payload);

  // Validate response
  check(res, {
    "status is 200": (r) => r.status === 200,
  });
}
