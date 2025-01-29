﻿using Microsoft.AspNetCore.Mvc;
using Mimer.Framework.Json;
using Mimer.Notes.Model.Requests;
using Mimer.Notes.Server;
using Mimer.Notes.WebApi.Base;
using System.Text;

namespace Mimer.Notes.WebApi.Controllers {
	[ApiController]
	[Route("api/note")]
	public class NoteController : MimerController {
		private MimerServer _server;

		public NoteController(MimerServer server) {
			_server = server;
		}

		[HttpPost("multi")]
		public async Task<IActionResult> Multi([FromBody] JsonObject json) {
			var response = await _server.MultiNote(new MultiNoteRequest(json));
			if (response == null) {
				return NotFound();
			}
			return Content(response.ToJsonString(), "text/plain", Encoding.UTF8);
		}

		[HttpPost("create")]
		public async Task<IActionResult> Create([FromBody] JsonObject json) {
			var response = await _server.CreateNote(new WriteNoteRequest(json));
			if (response == null) {
				return Conflict();
			}
			return Content(response.ToJsonString(), "text/plain", Encoding.UTF8);
		}

		[HttpPost("read")]
		public async Task<IActionResult> Read([FromBody] JsonObject json) {
			var response = await _server.ReadNote(new ReadNoteRequest(json));
			if (response == null) {
				return NotFound();
			}
			return Content(response.ToJsonString(), "text/plain", Encoding.UTF8);
		}

		[HttpPost("update")]
		public async Task<IActionResult> Update([FromBody] JsonObject json) {
			var response = await _server.UpdateNote(new WriteNoteRequest(json));
			if (response == null) {
				return NotFound();
			}
			return Content(response.ToJsonString(), "text/plain", Encoding.UTF8);
		}

		[HttpPost("delete")]
		public async Task<IActionResult> Delete([FromBody] JsonObject json) {
			var response = await _server.DeleteNote(new DeleteNoteRequest(json));
			if (response == null) {
				return NotFound();
			}
			return Content(response.ToJsonString(), "text/plain", Encoding.UTF8);
		}

		[HttpPost("share")]
		public async Task<IActionResult> Share([FromBody] JsonObject json) {
			var response = await _server.ShareNote(new ShareNoteRequest(json));
			if (response == null) {
				return Conflict();
			}
			return Content(response.ToJsonString(), "text/plain", Encoding.UTF8);
		}

		[HttpPost("share-offers")]
		public async Task<IActionResult> ReadShareOffers([FromBody] JsonObject json) {
			var response = await _server.ReadShareOffers(new BasicRequest(json));
			if (response == null) {
				return NotFound();
			}
			return Content(response.ToJsonString(), "text/plain", Encoding.UTF8);
		}

		[HttpPost("share/delete")]
		public async Task<IActionResult> DeleteShare([FromBody] JsonObject json) {
			var response = await _server.DeleteShare(new DeleteShareRequest(json));
			if (response == null) {
				return NotFound();
			}
			return Content(response.ToJsonString(), "text/plain", Encoding.UTF8);
		}



	}
}
