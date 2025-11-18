"use strict";

function notifyServiceWorker(){
	chrome.runtime.sendMessage({type: "update"}, function(response) {
	  return true;
	});
}

function normalizeIpList(rawIps) {
	return rawIps
		.split(",")
		.map(function (entry) {
			return entry.replace(/\s+/g, "").replace(/\|/g, "");
		})
		.filter(function (entry) {
			return entry.length > 0;
		});
}

function saveRule(domain_string, ips, manual, callback){
	var validInput = true;

	var ip_array = normalizeIpList(ips);
	if(ip_array.length === 0){
		validInput = false;
	}

	for (var i = 0; i < ip_array.length; i++){
		var ip = ip_array[i];
		if(ip.charAt(0) == "!"){
			if(!isIP(ip.substring(1,ip.length))){
				validInput = false;
			}
		}else{
			if(!isIP(ip)){
				validInput = false;
			}
		}
	}

	domain_string = domain_string.toLowerCase();

	if(!validInput){
		var errorMessage = "Please enter at least one valid IPv4 address (optionally prefixed with ! for blacklist).";
		if(typeof displayMessage === "function"){
			displayMessage("<span class=\"error\">"+errorMessage+"</span>");
		}else{
			alert(errorMessage);
		}
		if(callback != null){
			callback();
		}
		return;
	}

	if(validInput){
		var url_pointer = 0;
		var x;
		chrome.storage.local.get("urls", function (url_array) {
			url_array = url_array.urls;
			var already_url = false;
			
			if(url_array == null){
				url_array = [];
			}else{
				for(x=0; x < url_array.length; x++){
					if(url_array[x] === domain_string){
						already_url = true;
						url_pointer = x;
						break;
					}
				}
			}
			
			chrome.storage.local.get("allowed_ips", function(allowed_ips_array) {
				allowed_ips_array = allowed_ips_array.allowed_ips;
				
				if(!already_url){
					url_array[url_array.length] = domain_string;
					url_pointer = x++;
					chrome.storage.local.set({'urls': url_array}, function(){
						notifyServiceWorker();
					});
				}
				
				if(allowed_ips_array && !manual){
					if(allowed_ips_array[url_pointer] != null){
						var input_ips = ips.split(",");
						var stored_ips = allowed_ips_array[url_pointer].split(",");
						console.log("stored_ips: "+stored_ips);
						//prevent duplicate ip addresses or alternative ones
						for (var q = 0; q < stored_ips.length; q++){
							for (var y = 0; y < input_ips.length; y++){
								var i = input_ips[y];
								var s = stored_ips[q].replace(/\|/g,'');
								console.log("input ip"+i+" stored:"+s);
								//i == "!"+s - i is negative version of already stored ip
								//"!"+i == s - i is non negative version of already stored ip
								if(i == s || i == "!"+s || "!"+i == s){
									console.log("before: "+stored_ips);
									stored_ips.splice(stored_ips.indexOf(s), 1);
									console.log("after: "+stored_ips);
								}
							}
						}
						ips = stored_ips.concat(input_ips);
						console.log("final ips"+ips);
					}
				}
				ips = ip_array.join(",")+"|";
				if(url_pointer){
					allowed_ips_array[url_pointer] = ips;
				}else if(already_url){
					if(url_array && allowed_ips_array){
						allowed_ips_array[url_pointer] = ips;
					}else{
						allowed_ips_array = [ips];
					}
				}else{
					allowed_ips_array = [ips];
				}
				
				console.log("final:"+allowed_ips_array);
			
				chrome.storage.local.set({'allowed_ips': allowed_ips_array}, function(){
					notifyServiceWorker();
					if(callback != null){
						callback();
					}
				});
			});
		});
	}
}

function removeRule(domain_string){
	chrome.storage.local.get("urls", function (url_array) {
		url_array = url_array.urls;
		chrome.storage.local.get("allowed_ips", function(allowed_ips_array) {
			allowed_ips_array = allowed_ips_array.allowed_ips;
			for(var x=0; x < allowed_ips_array.length; x++){
				if(url_array[x] === domain_string){
					allowed_ips_array.splice(x, 1);
					url_array.splice(x, 1);
					break;
				}
			}
			chrome.storage.local.set({'urls': url_array}, function(){
				chrome.storage.local.set({'allowed_ips': allowed_ips_array}, function(){
					notifyServiceWorker();
				});
			});
		});
	});
}

function isIP(ipaddress)   
{  
 if (/^(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)$/.test(ipaddress))  
  {
	  return true;
  }else{
	  return false;	
  }
}

function getHostFromURL(url){
    var a = document.createElement('a');
    a.href = url;
	if(url.indexOf(a.hostname) != -1){
    	return a.hostname;
	}else{
		return null;
	}
}

function validURL(str) {
  var pattern = new RegExp('^(https?:\\/\\/)?'+ // protocol
  '((([a-z\\d]([a-z\\d-]*[a-z\\d])*)\\.?)+[a-z]{2,}|'+ // domain name
  '((\\d{1,3}\\.){3}\\d{1,3}))'+ // OR ip (v4) address
  '(\\:\\d+)?(\\/[-a-z\\d%_.~+]*)*'+ // port and path
  '(\\?[;&a-z\\d%_.~+=-]*)?'+ // query string
  '(\\#[-a-z\\d_]*)?$','i'); // fragment locator
  return pattern.test(str);
}

